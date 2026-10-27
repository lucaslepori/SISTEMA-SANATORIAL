# Prepara todo y abre la pantalla de prueba de la API en el navegador. SÓLO PARA UNA PC DE DESARROLLO.
#
# Uso, desde PowerShell en la carpeta del código:  .\iniciar.ps1
# Pide la contraseña del usuario "postgres" (o la toma de la variable SG_PG_CLAVE).
# Se puede correr todas las veces que haga falta: no duplica nada.
param(
    [string]$ClavePostgres = $env:SG_PG_CLAVE,
    [string]$UsuarioPostgres = 'postgres',
    [int]$PuertoPostgres = 5432,
    [string]$Base = 'sistema_gestion'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-Location -LiteralPath $PSScriptRoot
$Url = 'http://localhost:5080/scalar/'

function Paso([string]$texto) { Write-Host ''; Write-Host "== $texto" -ForegroundColor Cyan }
function Bien([string]$texto) { Write-Host "   OK: $texto" -ForegroundColor Green }
function Falla([string]$texto) {
    Write-Host ''
    Write-Host "NO SE PUDO SEGUIR: $texto" -ForegroundColor Red
    Write-Host ''
    Read-Host 'Copiá este mensaje y mandalo. Enter para cerrar'
    exit 1
}
function PuertoAbierto([int]$puerto) {
    $cliente = New-Object System.Net.Sockets.TcpClient
    try { return $cliente.ConnectAsync('127.0.0.1', $puerto).Wait(1500) } catch { return $false } finally { $cliente.Dispose() }
}
function Responde([string]$url) {
    try { return (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 } catch { return $false }
}
function RecargarPath {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}
function TieneDotnet10 {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { return $false }
    return [bool](& dotnet --list-sdks | Where-Object { $_ -match '^10\.' })
}

# ---------------------------------------------------------------------------------------------
Paso '1/6 Código'
if (-not (Test-Path 'SistemaGestion.sln') -or -not (Test-Path 'src\SistemaGestion.Api\Program.cs')) {
    Falla "Este archivo tiene que estar en la carpeta del código (la que tiene SistemaGestion.sln). Carpeta actual: $PSScriptRoot"
}
if (-not (Select-String -Path 'src\SistemaGestion.Api\Program.cs' -Pattern 'MapScalarApiReference' -Quiet)) {
    Falla 'Esta copia del código no tiene la pantalla de prueba. Bajá la rama turnos-ambulatorios: https://github.com/lucaslepori/SISTEMA-SANATORIAL/archive/refs/heads/turnos-ambulatorios.zip'
}
Bien $PSScriptRoot

# ---------------------------------------------------------------------------------------------
Paso '2/6 .NET 10 SDK'
if (-not (TieneDotnet10)) {
    Write-Host '   No está instalado el SDK de .NET 10.' -ForegroundColor Yellow
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        $r = Read-Host '   ¿Lo instalo ahora con winget? (S/N)'
        if ($r -match '^[sS]') {
            & winget install --id Microsoft.DotNet.SDK.10 -e --accept-package-agreements --accept-source-agreements
            RecargarPath
        }
    }
    if (-not (TieneDotnet10)) {
        Falla 'Instalá el .NET 10 SDK desde https://dotnet.microsoft.com/download/dotnet/10.0 (el "SDK", no el "Runtime") y volvé a ejecutar este archivo.'
    }
}
Bien ((& dotnet --list-sdks | Where-Object { $_ -match '^10\.' } | Select-Object -Last 1) -replace ' \[.*$', '')

# ---------------------------------------------------------------------------------------------
Paso '3/6 PostgreSQL'
if (-not (PuertoAbierto $PuertoPostgres)) {
    $servicios = @(Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue)
    if ($servicios.Count -eq 0) {
        Falla "No hay ningún PostgreSQL escuchando en el puerto $PuertoPostgres ni instalado como servicio. Instalalo desde https://www.postgresql.org/download/windows/ (versión 18) y volvé a ejecutar este archivo."
    }
    foreach ($s in $servicios) {
        Write-Host "   Iniciando el servicio $($s.Name)..."
        try { Start-Service -Name $s.Name } catch {
            Falla "El servicio $($s.Name) está detenido y no se pudo iniciar (hace falta ser administrador). Abrí 'Servicios' de Windows, buscá $($s.Name) y tocá 'Iniciar'."
        }
    }
    Start-Sleep -Seconds 3
    if (-not (PuertoAbierto $PuertoPostgres)) {
        Falla "PostgreSQL está instalado pero no responde en el puerto $PuertoPostgres. Si lo instalaste con otro puerto, ejecutá: .\iniciar.ps1 -PuertoPostgres NUMERO"
    }
}
Bien "responde en el puerto $PuertoPostgres"

# ---------------------------------------------------------------------------------------------
Paso '4/6 Compilar'
& dotnet build SistemaGestion.sln -nologo -v quiet
if ($LASTEXITCODE -ne 0) { Falla 'La compilación falló. El detalle está arriba.' }
Bien 'compilado'

# ---------------------------------------------------------------------------------------------
Paso "5/6 Base de datos '$Base' con datos de ejemplo"
if ([string]::IsNullOrEmpty($ClavePostgres)) {
    $segura = Read-Host "   Contraseña del usuario '$UsuarioPostgres' de PostgreSQL (la que elegiste al instalarlo)" -AsSecureString
    $ClavePostgres = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($segura))
}
$clave = $ClavePostgres.Replace('"', '""')
# Por variable de entorno y no como argumento: Windows PowerShell rompe las comillas de los argumentos.
$env:SG_DB_ADMIN = "Host=localhost;Port=$PuertoPostgres;Database=$Base;Username=$UsuarioPostgres;Password=`"$clave`""
& dotnet run --no-build --project src\SistemaGestion.Migraciones -- --crear-base --datos-desarrollo
$codigo = $LASTEXITCODE
Remove-Item Env:\SG_DB_ADMIN
if ($codigo -ne 0) {
    Falla "No se pudo preparar la base (el motivo está en la línea de arriba). Si dice 'password authentication failed', la contraseña de '$UsuarioPostgres' no es la correcta."
}
Bien 'tablas, usuarios locales y datos de ejemplo listos'

# ---------------------------------------------------------------------------------------------
Paso '6/6 API'
# La API usa los usuarios locales que se acaban de crear; la ventana nueva hereda estas variables.
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ConnectionStrings__Api = "Host=localhost;Port=$PuertoPostgres;Database=$Base;Username=sg_api_login;Password=sg_api_local"
$env:ConnectionStrings__Worker = "Host=localhost;Port=$PuertoPostgres;Database=$Base;Username=sg_worker_login;Password=sg_worker_local"

if (Responde $Url) {
    Write-Host '   Ya había una API corriendo: se usa esa. (Si cambiaste el código, cerrá su ventana y volvé a ejecutar esto.)' -ForegroundColor Yellow
} else {
    if (PuertoAbierto 5080) {
        Falla 'El puerto 5080 está ocupado por otro programa. Cerralo (o reiniciá la PC) y volvé a ejecutar este archivo.'
    }
    $comando = "Set-Location -LiteralPath '$PSScriptRoot'; `$host.UI.RawUI.WindowTitle = 'API Sistema sanatorial (cerrar esta ventana la apaga)'; dotnet run --no-build --project src\SistemaGestion.Api"
    Start-Process powershell -ArgumentList '-NoExit', '-NoProfile', '-Command', $comando | Out-Null
    Write-Host '   Arrancando en otra ventana (puede tardar hasta un minuto)...'
    $limite = (Get-Date).AddSeconds(90)
    while (-not (Responde $Url)) {
        if ((Get-Date) -gt $limite) {
            Falla "La API no respondió en 90 segundos. Mirá la ventana 'API Sistema sanatorial': el error está ahí."
        }
        Start-Sleep -Seconds 2
    }
}
if (-not (Responde 'http://localhost:5080/api/v1/catalogos/servicios')) {
    Falla 'La pantalla abre pero la API no puede leer la base. Cerrá la ventana de la API y volvé a ejecutar este archivo.'
}
Bien $Url

Start-Process $Url
Write-Host ''
Write-Host "Listo. La pantalla de prueba está en $Url" -ForegroundColor Green
Write-Host "La API sigue andando mientras esté abierta la ventana 'API Sistema sanatorial'."
Read-Host 'Enter para cerrar esta ventana'
