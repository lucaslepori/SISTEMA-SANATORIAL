# Sistema sanatorial

Sistema nuevo que reemplaza por módulos a Geclisa. Módulos actuales: pacientes, catálogos y turnos ambulatorios. Sigue historia clínica.

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
src/SistemaGestion.Migraciones/ Aplica las migraciones (y con --datos-desarrollo, carga usuarios y datos locales).
src/SistemaGestion.Importador/  Importa catálogos, agendas, pacientes y turnos desde Geclisa (sólo lee Geclisa).
src/Modulos/SistemaGestion.Pacientes/  Pacientes (API v1).
src/Modulos/SistemaGestion.Catalogos/  Profesionales, servicios, especialidades, sedes, consultorios y coberturas.
src/Modulos/SistemaGestion.Turnos/     Agendas, bloqueos, disponibilidad y turnos ambulatorios.
tests/SistemaGestion.Tests/ Pruebas de integración contra un PostgreSQL real.
docs/                      Decisiones y correspondencia con Geclisa.
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
| Un profesional no tiene dos turnos en el mismo horario | Restricción de exclusión en `turnos.turnos` y bloqueo por profesional al reservar | Evita el doble turno que hoy permite Geclisa |
| Un paciente no tiene dos turnos superpuestos | Restricción de exclusión en `turnos.turnos` | Integridad |
| Los turnos cancelados no se borran | Estado `CANCELADO` con motivo; sin permiso `DELETE` | Trazabilidad |
| Cada agenda la gestiona un solo sistema | `turnos.agendas.gestionada_por` | Convivencia con Geclisa |

## Usuarios de base

La aplicación nunca se conecta como dueña de la base.

| Usuario | Grupo | Puede |
| --- | --- | --- |
| `sg_admin` (o `postgres`) | dueño | Aplicar migraciones. Sólo se usa al desplegar. |
| `sg_api_login` | `sg_api` | Leer y escribir pacientes de la empresa fijada; agregar auditoría y eventos. No borra. |
| `sg_worker_login` | `sg_worker` | Leer la bandeja de eventos de todas las empresas y marcarlos como entregados. |

## Correr en local

Requisitos: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) y PostgreSQL 18 (instalado o con Docker).
**En Windows, lo más simple:** `.\iniciar.ps1` en la carpeta del código prepara la base, levanta la API y abre el navegador. Ver [docs/probar-en-windows.md](docs/probar-en-windows.md).

1. Base de datos. Con Docker: `docker compose up -d`. Sin Docker, crear una base `sistema_gestion` en tu PostgreSQL (por ejemplo desde pgAdmin).
2. Migraciones, usuarios locales de la aplicación y datos de ejemplo, con un usuario administrador (no hace falta `psql`):
   ```
   dotnet run --project src/SistemaGestion.Migraciones -- "Host=localhost;Database=sistema_gestion;Username=sg_admin;Password=sg_admin_local" --crear-base --datos-desarrollo
   ```
   Sin `--datos-desarrollo` sólo aplica las migraciones (es lo que se usa en producción, con `deploy/crear-roles.sql` para los usuarios).
3. API: `dotnet run --project src/SistemaGestion.Api`. Queda en http://localhost:5080.
4. Probar desde el navegador en http://localhost:5080/scalar (sólo en Development), o con `src/SistemaGestion.Api/SistemaGestion.Api.http`.

En desarrollo la API acepta el usuario por el encabezado `X-Usuario-Sujeto` en lugar de un token, y si no viene
usa `admin.local` en la empresa de ejemplo (`Autenticacion:Desarrollo` en `appsettings.Development.json`).
**Este modo se niega a arrancar fuera del entorno Development.** En producción se configura `Autenticacion:Authority` con el proveedor OpenID Connect (por ejemplo Keycloak).

## Importar desde Geclisa

```
SG_GECLISA="Server=...;Database=...;User Id=lector;Password=...;TrustServerCertificate=true" \
SG_DB_ADMIN="Host=localhost;Database=sistema_gestion;Username=sg_admin;Password=sg_admin_local" \
dotnet run --project src/SistemaGestion.Importador -- --empresa <id> [--simular] [--pacientes todos] [--datos-reales]
```

Sólo hace SELECT en Geclisa (igual, usar un login de sólo lectura). Escribe con el rol `sg_api` y la empresa fijada,
en una sola transacción, e informa lo que no pudo cargar. Pacientes seudonimizados salvo `--datos-reales`.
Las agendas quedan con `gestionada_por = GECLISA`. **No está probado contra una base real de Geclisa.**
Detalle en [docs/probar-en-windows.md](docs/probar-en-windows.md#6-opcional-traer-datos-de-geclisa).

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

## API v1 de catálogos

Escribe sólo el rol administrador; lee cualquier usuario de la empresa. No hay borrado: los datos se desactivan.

| Ruta | Métodos |
| --- | --- |
| `/api/v1/catalogos/servicios`, `/especialidades`, `/sedes`, `/consultorios`, `/coberturas`, `/profesionales` | `GET`, `POST` |
| `/api/v1/catalogos/coberturas/{id}/planes` | `GET`, `POST` |

Cada dato que existe en Geclisa acepta su `geclisaId` para el puente de sincronización.

## API v1 de turnos

Los horarios viajan en ISO 8601 con desplazamiento (`2026-11-02T09:00:00-03:00`). Las agendas se definen en hora local de la empresa.

| Método | Ruta | Roles | Notas |
| --- | --- | --- | --- |
| `POST` | `/api/v1/agendas` | administrador | Día de la semana (1 = lunes), franja, duración, vigencia, capacidad, sobreturnos y coberturas. Agenda superpuesta del mismo profesional: `409`. |
| `GET` | `/api/v1/agendas?profesionalId=` | todos | |
| `PUT` | `/api/v1/agendas/{id}/gestion` | administrador | Pasa la agenda de Geclisa al sistema nuevo o al revés. Exige `If-Match`. |
| `POST` | `/api/v1/bloqueos` | administrador | Licencia (con profesional) o feriado (sin profesional). Devuelve los turnos ya dados que quedan afectados. |
| `GET` | `/api/v1/turnos/disponibilidad?profesionalId=&servicioId=&coberturaId=&desde=&hasta=` | todos | Hasta 31 días. Sólo horarios futuros de agendas del sistema nuevo. |
| `POST` | `/api/v1/turnos` | admision, administrador | Horario ocupado: `409`. Fuera de la grilla, cobertura no aceptada o paciente inexistente: `422`. |
| `GET` | `/api/v1/turnos/{id}` | todos | |
| `GET` | `/api/v1/turnos?profesionalId=&fecha=` o `?pacienteId=&desde=` | todos | Agenda del día o turnos del paciente. |
| `POST` | `/api/v1/turnos/{id}/cancelacion` | admision, administrador | Exige motivo e `If-Match`. Sólo turnos reservados o confirmados. |
| `POST` | `/api/v1/turnos/{id}/estado` | según el estado | Reservado → confirmado → presente → atendido; reservado o confirmado → ausente. Sólo el profesional marca atendido. |

## Eventos de integración

| Tipo | Cuándo | Datos |
| --- | --- | --- |
| `pacientes.paciente_creado` | Alta de paciente | El paciente completo, como lo devuelve la API |
| `pacientes.paciente_modificado` | Modificación | El paciente después del cambio |
| `turnos.turno_asignado` | Turno dado | El turno |
| `turnos.turno_cancelado` | Cancelación | El turno, con el motivo |
| `turnos.turno_estado_cambiado` | Confirmado, presente, atendido o ausente | El turno |
| `turnos.agenda_gestion_cambiada` | Una agenda pasa de Geclisa al sistema nuevo o al revés | La agenda |

Se envían por `POST` JSON a las URLs de `Publicador:Webhooks`. La entrega es "al menos una vez": el suscriptor debe ignorar un `id` de evento que ya procesó.

## Cómo agregar un módulo

1. Una migración nueva `db/migraciones/V00N__<modulo>.sql` con su propio esquema, `empresa_id` en cada tabla, políticas de fila y permisos para `sg_api` sin `DELETE`.
2. Un proyecto en `src/Modulos/` con su `Map<Modulo>` y sus contratos versionados.
3. Ningún módulo lee las tablas de otro: usa su API o sus eventos.

## Pendiente

- Historia clínica (evoluciones de sólo agregar, enmiendas firmadas).
- Turnos: reprogramación, recordatorios, turnos web y práctica del nomenclador.
- Catálogos: modificación y baja lógica.
- Proveedor de identidad (Keycloak) y doble factor para profesionales.
- Puente con Geclisa: el importador hace una carga (repetible); falta la sincronización continua y probarlo contra una copia real. Ver [docs/turnos-geclisa.md](docs/turnos-geclisa.md).
- Fachada FHIR para la Red Nacional de Interoperabilidad.
