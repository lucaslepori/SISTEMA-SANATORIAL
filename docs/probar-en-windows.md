# Probar el módulo de turnos en una PC con Windows

Guía para probar sin `psql` ni pgAdmin: un script prepara todo y abre el navegador.
Tiempo estimado: 15 minutos la primera vez (casi todo es instalar PostgreSQL y .NET).

## 1. Lo que hace falta instalar una sola vez

1. **PostgreSQL 18** con el instalador de EDB: https://www.postgresql.org/download/windows/ .
   Anotá la contraseña que le ponés al usuario `postgres`; el script te la va a pedir.
2. **.NET 10 SDK**: https://dotnet.microsoft.com/download/dotnet/10.0 (el "SDK", no sólo el "Runtime").
   Si no lo tenés, el script ofrece instalarlo con `winget`.

## 2. Bajar el código

Bajá y descomprimí el ZIP de la rama de turnos:
https://github.com/lucaslepori/SISTEMA-SANATORIAL/archive/refs/heads/turnos-ambulatorios.zip
(o en GitHub Desktop, cambiá a la rama `turnos-ambulatorios`). La rama `main` todavía no tiene esta pantalla.

## 3. Ejecutar el script

En la carpeta del código (la que tiene `iniciar.ps1`): clic derecho en un lugar vacío → *Abrir en Terminal*
(o abrí PowerShell y hacé `cd` a esa carpeta). Después:

```powershell
.\iniciar.ps1
```

El script, en orden: revisa que el código sea el correcto, que esté .NET 10 y que PostgreSQL esté andando
(si el servicio está detenido intenta iniciarlo), te pide la contraseña de `postgres`, crea la base `sistema_gestion`
con tablas, usuarios locales y datos de ejemplo, levanta la API en otra ventana, espera a que responda y abre
el navegador en http://localhost:5080/scalar/ . Si algo falla, se detiene y dice qué hacer, en castellano.
Se puede ejecutar todas las veces que haga falta.

**Si Windows no deja ejecutar el script** ("la ejecución de scripts está deshabilitada" o "no está firmado digitalmente"):
Windows bloquea por defecto los scripts y, además, los que vienen en un ZIP bajado de internet. Para permitir
scripts locales sólo en tu usuario y desbloquear éste, en la misma ventana:

```powershell
Set-ExecutionPolicy -Scope CurrentUser RemoteSigned
Unblock-File .\iniciar.ps1
```

Es la configuración que recomienda Microsoft para desarrollo; se deshace con `Set-ExecutionPolicy -Scope CurrentUser Undefined`.

La API funciona mientras esté abierta la ventana "API Sistema sanatorial". Para apagarla, cerrala.
Entrás como `admin.local` en *Clínica Ejemplo S.A.* sin hacer nada; para probar con otro usuario, agregá el
encabezado `X-Usuario-Sujeto: admision.local`. Es un explorador de la API, no la pantalla final del sistema.

Opciones: `.\iniciar.ps1 -PuertoPostgres 5433` si PostgreSQL usa otro puerto; `-Base otro_nombre` para usar otra base.

### Hacerlo a mano (sin el script)

```powershell
$env:SG_DB_ADMIN = "Host=localhost;Database=sistema_gestion;Username=postgres;Password=TU_CLAVE"
dotnet run --project src\SistemaGestion.Migraciones -- --crear-base --datos-desarrollo
dotnet run --project src\SistemaGestion.Api
```

y abrir http://localhost:5080/scalar/ con la ventana de la API abierta.

## 4. Recorrido sugerido

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

## 5. Ver los datos en pgAdmin

Clic derecho en `sistema_gestion` → *Query Tool*:

```sql
SELECT t.horario, t.estado, p.apellidos, p.nombres
FROM turnos.turnos t JOIN pacientes.pacientes p ON p.id = t.paciente_id
ORDER BY lower(t.horario);

SELECT ocurrido_en, accion, recurso_tipo, detalle FROM auditoria.eventos ORDER BY id DESC LIMIT 20;
```

Como `postgres` es superusuario, ves los datos de todas las empresas. La API, en cambio, sólo ve los de la empresa del pedido.

## 6. (Opcional) Traer datos de Geclisa

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
