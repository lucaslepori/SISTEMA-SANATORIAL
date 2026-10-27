# Probar el módulo de turnos en una PC con Windows

Guía para probar sin `psql`: todo se hace con pgAdmin, PowerShell y el navegador.
Tiempo estimado: 20 minutos la primera vez.

## 1. Instalar

1. **PostgreSQL 18** con el instalador de EDB: https://www.postgresql.org/download/windows/ .
   Trae pgAdmin. Anotá la contraseña que le ponés al usuario `postgres`.
2. **.NET 10 SDK**: https://dotnet.microsoft.com/download/dotnet/10.0 (el "SDK", no sólo el "Runtime").
   Para verificar, en PowerShell: `dotnet --version` tiene que mostrar 10.0.algo.
3. **El código**: en GitHub Desktop, clonar `lucaslepori/SISTEMA-SANATORIAL` y cambiar a la rama `turnos-ambulatorios`
   (o descargar el ZIP de esa rama desde GitHub).

## 2. Crear la base

En pgAdmin: Servers → PostgreSQL 18 → clic derecho en *Databases* → *Create* → *Database...* → nombre `sistema_gestion` → *Save*.

## 3. Cargar tablas, usuarios y datos de ejemplo

En PowerShell, dentro de la carpeta del código (reemplazá `TU_CLAVE` por la contraseña de `postgres`):

```powershell
dotnet run --project src\SistemaGestion.Migraciones -- "Host=localhost;Database=sistema_gestion;Username=postgres;Password=TU_CLAVE" --datos-desarrollo
```

Tiene que terminar con `Usuarios locales y datos de ejemplo cargados.` Se puede repetir sin problema.

Esto crea:

- Las tablas de los módulos (pacientes, catálogos, turnos, auditoría).
- Los usuarios de base `sg_api_login` y `sg_worker_login` con contraseñas locales (`sg_api_local`, `sg_worker_local`). **Sólo para tu PC.**
- Datos de ejemplo: la empresa *Clínica Ejemplo S.A.*, los usuarios `admin.local`, `admision.local` y `medica.local`,
  un servicio, una especialidad, una sede, un consultorio, la cobertura *Particular*, una profesional (*Dra. Local*)
  y su agenda de lunes y miércoles de 9 a 12 con turnos de 20 minutos.

## 4. Levantar la API y abrir la pantalla de prueba

```powershell
dotnet run --project src\SistemaGestion.Api
```

Dejá esa ventana de PowerShell abierta: la API funciona mientras el comando sigue corriendo (se corta con Ctrl+C).
Cuando arrancó bien, las últimas líneas dicen `Now listening on: http://localhost:5080` y `Pantalla de prueba: http://localhost:5080/scalar/`.

Abrí http://localhost:5080/scalar/ . Es un explorador de la API (no es la pantalla final del sistema):
lista cada operación, arma el pedido y muestra la respuesta. Entrás como `admin.local` en *Clínica Ejemplo S.A.*
sin hacer nada; para probar con otro usuario, agregá el encabezado `X-Usuario-Sujeto: admision.local`.

### Si no abre

| Qué ves | Causa probable | Qué hacer |
|---|---|---|
| El navegador dice "no se puede acceder" / `ERR_CONNECTION_REFUSED` | La API no está corriendo: el comando terminó con error o se cerró la ventana | Mirá el error en PowerShell (ver abajo) |
| PowerShell: `A compatible .NET SDK was not found` | Falta el SDK 10 | Instalar el **SDK** 10 (paso 1) y abrir una ventana nueva |
| PowerShell: `Project file does not exist` | No estás en la carpeta del código | `cd` a la carpeta que tiene `SistemaGestion.sln` |
| Página en blanco con error **404** | Estás en la rama `main`, que todavía no tiene esta pantalla | Cambiar a la rama `turnos-ambulatorios` (o bajar el ZIP de esa rama). Si existe la carpeta `src\SistemaGestion.Importador`, estás en la rama correcta |
| `address already in use` / puerto 5080 ocupado | Ya hay otra API abierta | Cerrar la otra ventana de PowerShell |
| La pantalla abre pero las operaciones dan **500** | La API no se conecta a la base | Repetir el paso 3 y revisar que PostgreSQL esté iniciado |

## 5. Recorrido sugerido

1. **Catálogos → GET /api/v1/catalogos/profesionales**: aparece la Dra. Local.
2. **Agendas → GET /api/v1/agendas**: su agenda (id `0192f000-0000-7000-8000-0000000000c1` para los lunes).
3. **Pacientes → POST /api/v1/pacientes** con:
   ```json
   { "apellidos": "Prueba", "nombres": "Ana", "fechaNacimiento": "1985-06-20", "sexoRegistral": "F",
     "identificadores": [ { "sistema": "DNI", "valor": "30111222" } ] }
   ```
   Copiá el `id` que devuelve.
4. **Turnos → GET /api/v1/turnos/disponibilidad** con `profesionalId=0192f000-0000-7000-8000-0000000000b6`
   y un rango de fechas de la semana que viene: lista los horarios libres.
5. **Turnos → POST /api/v1/turnos** con la agenda, el paciente y uno de esos horarios
   (por ejemplo `"inicio": "2026-11-02T09:20:00-03:00"`): devuelve 201.
6. Repetí el mismo POST con otro paciente: devuelve **409**, porque el horario ya está ocupado. Ese es el control de doble turno.
7. **POST /api/v1/turnos/{id}/cancelacion** con `{ "motivo": "Prueba" }` y el encabezado `If-Match: "1"`
   (la versión del turno, que viene en el campo `version` y en el encabezado `ETag`; sin él responde 428, para que
   dos personas no pisen el mismo cambio). El turno queda cancelado (no se borra) y el horario vuelve a estar libre.

## 6. Ver los datos en pgAdmin

Clic derecho en `sistema_gestion` → *Query Tool*:

```sql
SELECT t.horario, t.estado, p.apellidos, p.nombres
FROM turnos.turnos t JOIN pacientes.pacientes p ON p.id = t.paciente_id
ORDER BY lower(t.horario);

SELECT ocurrido_en, accion, recurso_tipo, detalle FROM auditoria.eventos ORDER BY id DESC LIMIT 20;
```

Como `postgres` es superusuario, ves los datos de todas las empresas. La API, en cambio, sólo ve los de la empresa del pedido.

## 7. (Opcional) Traer datos de Geclisa

El importador **sólo lee** Geclisa y escribe en la base nueva. Recomendaciones antes de usarlo:

- Correrlo contra una **copia** (restore de un backup) de Geclisa, no contra la base en producción.
- Usar un login de SQL Server con permiso de **sólo lectura** (rol `db_datareader`).
- **No fue probado contra una base real de Geclisa**: las consultas salen del script del esquema. Lo primero es correrlo con `--simular`.

```powershell
$env:SG_GECLISA = "Server=SERVIDOR\INSTANCIA;Database=NOMBRE_BASE;User Id=lector;Password=...;TrustServerCertificate=true"
$env:SG_DB_ADMIN = "Host=localhost;Database=sistema_gestion;Username=postgres;Password=TU_CLAVE"
dotnet run --project src\SistemaGestion.Importador -- --empresa 0192f000-0000-7000-8000-000000000001 --simular
```

(`TrustServerCertificate=true` acepta el certificado propio del SQL Server; está bien en una red interna para una prueba,
no para producción. Con usuario de Windows: `Integrated Security=true` en lugar de `User Id`/`Password`.)

Qué trae: ubicaciones, consultorios, servicios, especialidades, prestadores y sus especialidades, obras sociales y planes,
cronogramas vigentes (como agendas que **sigue manejando Geclisa**: la API no da turnos en ellas),
coberturas por cronograma, feriados futuros, los pacientes con turnos futuros y esos turnos.

Por defecto los pacientes se cargan **seudonimizados**: "Paciente Geclisa 1234", sólo el año de nacimiento,
sin DNI, teléfono, correo, número de afiliado ni observaciones. Para copiar los datos reales hay que agregar
`--datos-reales` (Ley 25.326: hacerlo sólo en un equipo protegido). `--pacientes todos` trae todas las fichas, no sólo las que tienen turnos.

Al final muestra un informe: cuánto cargó y qué no pudo cargar, con los ids de Geclisa para revisarlos.
Ejemplos de lo que informa: cronogramas superpuestos, días con nombre desconocido, prestadores sin matrícula,
fichas sin sexo válido y **dobles turnos que Geclisa dejó pasar**. Sin `--simular` guarda los datos;
se puede volver a correr y actualiza en lugar de duplicar (y cancela los turnos que se borraron en Geclisa).
