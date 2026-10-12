# Koduma — compatibilidad con Windows

## Contexto y objetivo

Una revisión de la base de código (a petición del usuario, no derivada de ningún spec anterior) encontró varios puntos donde Koduma asume un entorno Unix/Linux de forma implícita. Este documento cubre los arreglos concretos necesarios para que el CLI, el servidor MCP y la suite de tests funcionen correctamente en Windows, sin reabrir la lógica de seguridad del algoritmo de resolución de symlinks del MCP (`Canonicalize` en `KodumaMcpServer.cs`), que ya pasó por 4 rondas de revisión adversarial y no debe reestructurarse.

## Alcance

**Incluido:**
1. Resolución del ejecutable de ESLint apropiada para Windows (`eslint.cmd` en vez de `eslint`).
2. Manejo portable de separadores de ruta y comparación de mayúsculas/minúsculas apropiada por plataforma en los helpers de `KodumaMcpServer.cs` (`SplitSegments`, `GetRoot`, `IsWithinRoot`) — sin tocar el bucle de `Canonicalize` en sí.
3. Portabilidad del test de integración end-to-end (RID dinámico, sufijo `.exe` del binario publicado en Windows).
4. Documentar como requisito de entorno (no como código) que los tests que crean symlinks reales necesitan modo de desarrollador o privilegios de administrador en Windows.

**Fuera de alcance:**
- Cualquier cambio a la lógica de resolución de symlinks (`Canonicalize`, el salto por segmento, el tope de 40 saltos) más allá de hacer sus helpers de manejo de strings portables. Esa lógica ya está verificada y no se reabre.
- Añadir infraestructura de CI (no existe ninguna en este repo).
- Lógica de "skip" automático en los tests que requieren symlinks — es una responsabilidad del entorno, no del código de test.
- Verificación real en una máquina Windows: todo lo de abajo se implementa y se razona por inspección de código desde este entorno Linux; no hay manera de ejecutar/confirmar empíricamente el comportamiento real en Windows desde aquí. Esto se señala explícitamente donde aplica.

## 1. Resolución del ejecutable de ESLint (y su invocación segura en Windows)

`EslintAnalyzer` invoca hoy el proceso `"eslint"` directamente. En Windows, npm instala los CLIs como scripts `.cmd` (`eslint.cmd`), y `Process.Start` con `UseShellExecute = false` no resuelve extensiones de shim a partir del nombre desnudo — un problema conocido al invocar herramientas de Node desde .NET en Windows.

```csharp
internal static string ResolveEslintExecutable(bool isWindows) => isWindows ? "eslint.cmd" : "eslint";

private static readonly string EslintExecutable = ResolveEslintExecutable(OperatingSystem.IsWindows());
```

El método se expone como `internal static` (no `private`) específicamente para que un test pueda llamarlo con un valor fijo de `isWindows` y verificar la lógica de forma determinista, sin depender de en qué sistema operativo corre realmente la suite — necesita `InternalsVisibleTo` hacia el proyecto de test.

**Corrección posterior (importante):** resolver el nombre a `eslint.cmd` no basta. Con `UseShellExecute = false`, `CreateProcess` SÍ puede lanzar un `.cmd`/`.bat`, pero solo de forma implícita: por debajo arranca `cmd.exe` con la cadena de argumentos cruda, sin que quien invoca controle esa capa — exactamente la clase de inyección de comandos documentada como "BatBadBut", que este plan evita enrutando explícitamente por `cmd.exe` nosotros mismos (con nuestro propio escapado y lista negra) en vez de dejar que .NET lo haga implícitamente con el escapado de `ArgumentList`. Por eso de todos modos hace falta enrutar explícitamente por `cmd.exe`: así se controla exactamente qué línea de comandos ve el shell. Pero `cmd.exe` reinterpreta esa línea de comandos con su propia gramática (`&`, `|`, `^`, `%`, paréntesis, comillas), y el escapado a nivel de `ArgumentList` de .NET (correcto para invocar un proceso directamente) no protege contra esa segunda capa de interpretación del shell. Investigación sobre cómo resuelven esto otros runtimes (Rust, Go, OpenJDK, PHP) confirma que es un problema de doble escapado sin solución perfecta conocida — Rust documenta explícitamente que puede no ser posible escapar con seguridad algunos caracteres, y una librería dedicada a esto (`shescape`) tuvo una vulnerabilidad real por un paréntesis sin escapar.

**Decisión:** en vez de intentar escapar todo, `ExternalProcessRunner` rechaza con un error claro cualquier argumento que contenga un carácter especial de cmd.exe (`& | ^ % < > ( ) " ! \r \n \0`) antes de enrutar por el shell, y solo si ningún argumento lo contiene, invoca `cmd.exe /d /s /c "<comando entre comillas>"` (el patrón `/s` + comillas dobles envolventes documentado como la mitigación estándar, no como una garantía absoluta). Esto solo se activa en Windows y solo cuando el ejecutable termina en `.cmd`/`.bat` — en Linux/macOS el comportamiento no cambia (sigue siendo invocación directa sin shell, sin riesgo de inyección). El riesgo residual (algún caso límite de cmd.exe no cubierto por esta lista) queda documentado aquí, en la misma categoría que el TOCTOU/magic-link del servidor MCP: mitigado con la práctica estándar conocida, no eliminado con una garantía matemática.

Consecuencia práctica aceptada de esta lista negra: rechaza también rutas legítimas de Windows que contengan paréntesis o `&` (p. ej. `C:\Users\Jo (Work)\repo`). Es un trade-off deliberado del usuario — preferir un falso rechazo seguro sobre un escapado parcial — y no se reabre en esta ronda.

Consecuencia adicional de esta misma lista negra, no escrita hasta ahora: como todos los archivos descubiertos se pasan a una única invocación de ESLint, basta con que **uno solo** de los archivos analizados tenga un carácter prohibido en su nombre (p. ej. `file - Copy (2).js`) para que **todo** el análisis JS/TS de esa ejecución falle como un único error. Cae dentro del trade-off ya aceptado de "fallar de forma ruidosa en vez de silenciosa", pero no es un defecto a corregir en este plan — dividir en varias invocaciones de ESLint para aislar un nombre de archivo problemático es un cambio de diseño mayor, fuera de alcance.

`cmd.exe` se resuelve vía `ComSpec` en vez de con el nombre desnudo `"cmd.exe"`: con `UseShellExecute = false`, `CreateProcess` busca primero en el directorio del propio ejecutable que lanza el proceso y en el directorio de trabajo del proceso padre (es decir, de Koduma) antes que en `PATH`/`System32` — no en el `WorkingDirectory` que se pasa al `ProcessStartInfo` del proceso hijo (el repo bajo análisis). Aun así, un nombre desnudo sigue siendo plantable (binary planting) si un archivo `cmd.exe` termina en el directorio real de Koduma o de su proceso padre. `ComSpec` normalmente contiene la ruta absoluta de confianza (`C:\Windows\system32\cmd.exe`); si está vacío o sin definir, se cae a construir esa misma ruta desde `Environment.SpecialFolder.System`.

**Riesgo residual aceptado, sin cerrar en esta ronda:** el fix de `ComSpec` anterior solo protege la resolución de `cmd.exe` en sí. Los nombres desnudos `eslint.cmd`/`eslint`/`node` que `cmd.exe` termina invocando por debajo siguen sujetos al mismo orden de búsqueda de `CreateProcess` con prioridad al directorio actual, y por tanto siguen siendo plantables en principio. Como mitigación adicional de bajo costo se fija `NoDefaultCurrentDirectoryInExePath=1` en el entorno del proceso para este camino, lo cual reduce pero no cierra del todo ese riesgo. Puesto en contexto: el analizador de ESLint ya corre sin `--no-config-lookup`, es decir que carga y ejecuta directamente el `eslint.config.js` del repositorio analizado (no confiable) y cualquier plugin que referencie — esto ya es ejecución de código arbitrario al analizar un repositorio no confiable, en cualquier plataforma, independientemente de Windows. El riesgo de plantado de nombre desnudo que queda sin cerrar es, por tanto, una adición marginal sobre un modelo de amenaza ya aceptado, no un riesgo aislado nuevo — este es el dato material para quien deba decidir si el residuo es aceptable.

```csharp
internal const int CmdCommandLineLimit = 8191;

internal static bool IsCmdShim(string executable) =>
    executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

internal static string? FindUnsafeArgument(IReadOnlyList<string> arguments) =>
    arguments.FirstOrDefault(a => a.IndexOfAny(new[] { '&', '|', '^', '%', '<', '>', '(', ')', '"', '!', '\r', '\n', '\0' }) >= 0);

internal static string ResolveCmdExecutable()
{
    var comSpec = Environment.GetEnvironmentVariable("ComSpec");
    return string.IsNullOrEmpty(comSpec)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")
        : comSpec;
}

internal static (string Executable, IReadOnlyList<string> Arguments) RerouteThroughCmd(string executable, IReadOnlyList<string> arguments)
{
    var parts = new List<string> { executable };
    parts.AddRange(arguments);
    var commandLine = "\"" + string.Join(" ", parts.Select(p => "\"" + p + "\"")) + "\"";
    return (ResolveCmdExecutable(), new[] { "/d", "/s", "/c", commandLine });
}

internal static string? CheckCommandLineLength(IReadOnlyList<string> arguments, string fullCommandLine)
{
    if (fullCommandLine.Length <= CmdCommandLineLimit)
        return null;

    return $"Command line for cmd.exe would be {fullCommandLine.Length} characters across {arguments.Count} arguments, " +
           $"exceeding cmd.exe's {CmdCommandLineLimit}-character limit; the command was not run. " +
           "Reduce the number of files analyzed in a single invocation.";
}
```

`RunAsync` llama a `FindUnsafeArgument` antes de `RerouteThroughCmd`, y solo cuando `OperatingSystem.IsWindows() && IsCmdShim(executable)`. Como `"` está en la lista de caracteres rechazados, ningún argumento que llegue a `RerouteThroughCmd` puede contener comillas, así que no hace falta duplicar comillas internas al construir la línea de comandos.

**Límite de longitud de la línea de comandos de cmd.exe.** Cuando se enruta por `cmd.exe`, todos los argumentos (en el caso de ESLint, todas las rutas de archivo a analizar) viajan en una sola cadena `/c "<comando>"`, a diferencia del camino normal (`ArgumentList`), que no tiene este límite práctico. `cmd.exe` tiene un límite documentado de ~8191 caracteres en su línea de comandos; un repositorio con suficientes archivos JS/TS (del orden de 130+ con rutas absolutas típicas) puede superarlo. Si eso ocurriera sin detección, el fallo del proceso podría manifestarse de forma indistinguible de "ESLint corrió y no encontró problemas" ante el manejo de código de salida de `EslintAnalyzer` — el peor modo de fallo posible para una herramienta de calidad de código. Por eso `RunAsync` calcula la línea de comandos completa y, antes de invocar `Process.Start`, llama a `CheckCommandLineLength`: si excede el límite, devuelve `(-1, "", <mensaje>)` sin intentar la invocación, convirtiendo un posible fallo silencioso en un `AnalysisError` visible y reportable. Resolver el problema de fondo (repos grandes que no caben en una sola invocación) — por ejemplo, dividiendo la llamada a ESLint en varios lotes — queda fuera de alcance de esta ronda; es una decisión de diseño real que corresponde al usuario, no algo para resolver implícitamente aquí.

## 2. Portabilidad en `KodumaMcpServer.cs`

Dos problemas puntuales en los helpers que usa `Canonicalize`, sin tocar `Canonicalize` mismo:

**a) División de segmentos solo por `DirectorySeparatorChar`.** El texto crudo de un symlink (`FileSystemInfo.LinkTarget`) no pasa por `Path.GetFullPath`, así que si contiene el separador alternativo (`/` en Windows, donde el separador principal es `\`), `SplitSegments` lo trataría como un único segmento en vez de dividirlo. Arreglo: dividir por ambos separadores.

**b) Comparación `Ordinal` en `IsWithinRoot`.** NTFS es insensible a mayúsculas por defecto; comparar con `Ordinal` en Windows puede rechazar rutas legítimas cuya capitalización difiera de como se escribió `--root` (falla "cerrado" — no es un agujero de seguridad, es un falso rechazo). En Linux debe seguir siendo sensible a mayúsculas, porque ahí dos rutas con distinta capitalización SÍ son archivos distintos.

```csharp
private static string GetRoot(string path)
{
    var root = Path.GetPathRoot(path) ?? Path.DirectorySeparatorChar.ToString();
    var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    return trimmed.Length == 0 ? Path.DirectorySeparatorChar.ToString() : trimmed;
}

private static IEnumerable<string> SplitSegments(string path)
{
    var root = Path.GetPathRoot(path) ?? Path.DirectorySeparatorChar.ToString();
    return path.Substring(root.Length).Split(
        new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
        StringSplitOptions.RemoveEmptyEntries);
}

internal static bool IsWithinRoot(string candidate, string fullRoot)
{
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
    return string.Equals(candidate, fullRoot, comparison) || candidate.StartsWith(prefix, comparison);
}
```

`IsWithinRoot` pasa de `private` a `internal` por la misma razón que `ResolveEslintExecutable`: permite un test directo y determinista de la lógica de comparación sin depender de en qué plataforma corre la suite.

**Nota honesta de alcance:** en Linux, `Path.AltDirectorySeparatorChar` es idéntico a `Path.DirectorySeparatorChar` (ambos `/`), así que el cambio (a) no se puede verificar de forma significativa con un test automatizado desde este entorno — el código es correcto e inofensivo en Linux, pero su efecto real solo se manifiesta en Windows. Se documenta como revisado por inspección, no como cubierto por test.

## 3. Portabilidad del test de integración

`EndToEndAnalyzeTests.cs` tiene hardcodeado `-r linux-x64` al publicar el binario, y asume que el ejecutable publicado se llama `Koduma.Cli` sin extensión — en Windows el publish AOT produce `Koduma.Cli.exe`.

```csharp
using System.Runtime.InteropServices;
// ...
var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
var rid = $"{os}-{RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()}";
Arguments = $"publish \"{...}\" -c Release -r {rid} --self-contained -o \"{_publishDir}\"",
```

```csharp
var exeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
var binary = Path.Combine(_publishDir, "Koduma.Cli" + exeSuffix);
```

`RuntimeInformation.RuntimeIdentifier` **no** sirve directamente aquí: en Linux devuelve un RID específico de la distro (p. ej. `ubuntu.25.10-x64`), que `dotnet publish -r` rechaza porque no es uno de los RID portables que el SDK conoce. El enfoque implementado (en `tests/Koduma.Integration.Tests/EndToEndAnalyzeTests.cs`) construye el RID a mano como `{os}-{arquitectura}`, usando `OperatingSystem.IsWindows()`/`IsMacOS()` para el sistema operativo y `RuntimeInformation.ProcessArchitecture` para la arquitectura.

## 4. Requisito de entorno para tests con symlinks (documentación, no código)

Crear symlinks reales vía `Directory.CreateSymbolicLink`/`File.CreateSymbolicLink` en Windows requiere modo de desarrollador activado o ejecutar como administrador — sin eso, esos tests (en `KodumaMcpServerTests.cs` y `FileDiscoveryTests.cs`) fallan al preparar su fixture, no al hacer la aserción. Esto es una configuración del entorno de ejecución, no algo que el código de test deba resolver con lógica de "skip" — se documenta aquí y en un comentario breve en ambos archivos de test, nada más.

Adicionalmente: el test de integración (e2e) publica el CLI con Native AOT (`PublishAot=true`), lo cual en Windows requiere tener instalado el workload de Visual Studio "Desktop development with C++" (el requisito equivalente en macOS son las herramientas de línea de comandos de Xcode) — sin eso, el propio paso de `publish` falla antes de que el test pueda ejecutarse.
