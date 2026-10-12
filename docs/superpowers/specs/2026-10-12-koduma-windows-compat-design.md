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

## 1. Resolución del ejecutable de ESLint

`EslintAnalyzer` invoca hoy el proceso `"eslint"` directamente. En Windows, npm instala los CLIs como scripts `.cmd` (`eslint.cmd`), y `Process.Start` con `UseShellExecute = false` no resuelve extensiones de shim a partir del nombre desnudo — un problema conocido al invocar herramientas de Node desde .NET en Windows.

```csharp
internal static string ResolveEslintExecutable(bool isWindows) => isWindows ? "eslint.cmd" : "eslint";

private static readonly string EslintExecutable = ResolveEslintExecutable(OperatingSystem.IsWindows());
```

El método se expone como `internal static` (no `private`) específicamente para que un test pueda llamarlo con un valor fijo de `isWindows` y verificar la lógica de forma determinista, sin depender de en qué sistema operativo corre realmente la suite — necesita `InternalsVisibleTo` hacia el proyecto de test.

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
Arguments = $"publish \"{...}\" -c Release -r {RuntimeInformation.RuntimeIdentifier} --self-contained -o \"{_publishDir}\"",
```

```csharp
var exeSuffix = OperatingSystem.IsWindows() ? ".exe" : "";
var binary = Path.Combine(_publishDir, "Koduma.Cli" + exeSuffix);
```

`RuntimeInformation.RuntimeIdentifier` da directamente el RID de la plataforma donde corre el test (`linux-x64`, `win-x64`, etc.), sin necesitar un switch manual por sistema operativo.

## 4. Requisito de entorno para tests con symlinks (documentación, no código)

Crear symlinks reales vía `Directory.CreateSymbolicLink`/`File.CreateSymbolicLink` en Windows requiere modo de desarrollador activado o ejecutar como administrador — sin eso, esos tests (en `KodumaMcpServerTests.cs` y `FileDiscoveryTests.cs`) fallan al preparar su fixture, no al hacer la aserción. Esto es una configuración del entorno de ejecución, no algo que el código de test deba resolver con lógica de "skip" — se documenta aquí y en un comentario breve en ambos archivos de test, nada más.
