# Sistema de gestión clínica

Sistema nuevo que reemplaza por módulos a Geclisa. Empieza por pacientes; siguen turnos e historia clínica.

- **Base de datos:** PostgreSQL 18, con seguridad a nivel de fila por empresa.
- **Backend:** C# / .NET 10 (LTS) y ASP.NET Core.
- **Arquitectura:** monolito modular. Cada módulo tiene su propio esquema de base y sólo se comunica con los demás por su API o por eventos.

La propuesta completa (comparación de alternativas y fuentes) está en el documento "Propuesta de stack y arquitectura del sistema nuevo" del proyecto.

## Estructura

```
db/migraciones/            Scripts SQL versionados (V001__..., V002__...). Nunca se editan una vez aplicados.
deploy/                    Creación de usuarios de base y datos de desarrollo.
src/SistemaGestion.Api/    Programa principal: autenticación, contexto de empresa y registro de módulos.
src/SistemaGestion.Compartido/  Sesión de base con empresa fijada, auditoría, bandeja de eventos y publicador.
src/SistemaGestion.Migraciones/ Aplica las migraciones.
src/Modulos/SistemaGestion.Pacientes/  Módulo de pacientes (API v1).
tests/                     Pruebas de integración contra un PostgreSQL real.
```

## Reglas que el código ya aplica

| Regla | Dónde se aplica | Por qué |
| --- | --- | --- |
| Cada empresa ve sólo sus datos | Políticas de fila en PostgreSQL (`FORCE ROW LEVEL SECURITY`). Sin empresa fijada, la base no devuelve filas. | Multiempresa; Ley 25.326 |
| Un usuario por persona, con roles por empresa | `nucleo.usuarios` y `nucleo.usuarios_empresas`, vinculados al `sub` del proveedor OpenID Connect | Res. AAIP 47/2018 |
| Quién vio y quién cambió cada paciente | `auditoria.eventos`, en la misma transacción que la operación | Ley 26.529; Decreto 393/2023 |
| La auditoría no se puede alterar | Disparador que rechaza `UPDATE`, `DELETE` y `TRUNCATE`, incluso al dueño de la base | Ley 26.529; Decreto 393/2023 |
| Los pacientes no se borran | La aplicación no tiene permiso `DELETE` | Conservación de la HC |
| Dos personas no pisan el mismo cambio | Versión en cada paciente; `PUT` exige `If-Match` | Integridad |
| Los otros sistemas se enteran de cada cambio | Bandeja de salida (outbox) y publicador por webhooks | Integración con Geclisa y otros proyectos |
| Sexo registral F, M o X | Restricción en la base | Ley 26.743; Decreto 476/2021 |

## Usuarios de base

La aplicación nunca se conecta como dueña de la base.

| Usuario | Grupo | Puede |
| --- | --- | --- |
| `sg_admin` (o `postgres`) | dueño | Aplicar migraciones. Sólo se usa al desplegar. |
| `sg_api_login` | `sg_api` | Leer y escribir pacientes de la empresa fijada; agregar auditoría y eventos. No borra. |
| `sg_worker_login` | `sg_worker` | Leer la bandeja de eventos de todas las empresas y marcarlos como entregados. |

## Correr en local

Requisitos: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) y PostgreSQL 18 (instalado o con Docker).

1. Base de datos. Con Docker: `docker compose up -d`. Sin Docker, crear una base `sistema_gestion` en tu PostgreSQL (por ejemplo desde pgAdmin).
2. Migraciones, con un usuario administrador:
   ```
   dotnet run --project src/SistemaGestion.Migraciones -- "Host=localhost;Database=sistema_gestion;Username=sg_admin;Password=sg_admin_local"
   ```
3. Usuarios de la aplicación y datos de prueba:
   ```
   psql -h localhost -U sg_admin -d sistema_gestion -f deploy/crear-roles.sql -f deploy/datos-desarrollo.sql
   ```
4. API: `dotnet run --project src/SistemaGestion.Api`. Queda en http://localhost:5080. La especificación OpenAPI está en http://localhost:5080/openapi/v1.json.
5. Probar con `src/SistemaGestion.Api/SistemaGestion.Api.http`.

En desarrollo la API acepta el usuario por el encabezado `X-Usuario-Sujeto` en lugar de un token. **Este modo se niega a arrancar fuera del entorno Development.** En producción se configura `Autenticacion:Authority` con el proveedor OpenID Connect (por ejemplo Keycloak).

## Pruebas

Necesitan un PostgreSQL con un usuario administrador. Cada corrida crea una base nueva y la borra al terminar.

```
SG_TEST_DB_ADMIN="Host=localhost;Username=postgres;Password=postgres;Database=postgres" dotnet test
```

En Windows (PowerShell): `$env:SG_TEST_DB_ADMIN="Host=localhost;Username=postgres;Password=...;Database=postgres"; dotnet test`

## API v1 de pacientes

Todas las llamadas llevan el encabezado `X-Empresa-Id` con la empresa en la que se trabaja.

| Método | Ruta | Roles | Notas |
| --- | --- | --- | --- |
| `POST` | `/api/v1/pacientes` | admision, administrador | Devuelve `201`, `Location` y `ETag`. DNI repetido en la empresa: `409`. |
| `GET` | `/api/v1/pacientes/{id}` | admision, profesional, administrador | Registra la lectura en la auditoría. |
| `GET` | `/api/v1/pacientes?documento=` o `?apellidos=` | admision, profesional, administrador | Exige un criterio; no lista el padrón completo. Máximo 50 resultados. |
| `PUT` | `/api/v1/pacientes/{id}` | admision, administrador | Exige `If-Match: "<version>"`. Versión vieja: `412`. |

## Eventos de integración

| Tipo | Cuándo | Datos |
| --- | --- | --- |
| `pacientes.paciente_creado` | Alta de paciente | El paciente completo, como lo devuelve la API |
| `pacientes.paciente_modificado` | Modificación | El paciente después del cambio |

Se envían por `POST` JSON a las URLs de `Publicador:Webhooks`. La entrega es "al menos una vez": el suscriptor debe ignorar un `id` de evento que ya procesó.

## Cómo agregar un módulo

1. Una migración nueva `db/migraciones/V00N__<modulo>.sql` con su propio esquema, `empresa_id` en cada tabla, políticas de fila y permisos para `sg_api` sin `DELETE`.
2. Un proyecto en `src/Modulos/` con su `Map<Modulo>` y sus contratos versionados.
3. Ningún módulo lee las tablas de otro: usa su API o sus eventos.

## Pendiente

- Módulos de turnos e historia clínica (evoluciones de sólo agregar, enmiendas firmadas).
- Proveedor de identidad (Keycloak) y doble factor para profesionales.
- Puente con Geclisa: importar fichas y mantener sincronizados los pacientes.
- Fachada FHIR para la Red Nacional de Interoperabilidad.
