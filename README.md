# Koduma

Motor de calidad de código multi-lenguaje (al estilo SonarQube), escrito en .NET. Se distribuye como un único binario Native AOT que funciona como:

- **CLI** (`koduma analyze ./repo`) para uso interactivo y en CI.
- **Servidor MCP** (`koduma mcp-serve`) para que agentes (Claude Code, etc.) invoquen el análisis como herramienta.

Ambos comparten el mismo proceso y el mismo motor de dominio (`Koduma.Core`) — no hay comunicación por subprocesos entre ellos.

## Alcance (v1)

- Arquitectura multi-lenguaje basada en plugins.
- Análisis de **C#** nativo in-proc vía Roslyn (20 reglas propias) y, además, los **analizadores de .NET** (`CAxxxx`) y avisos del compilador vía `dotnet build`.
- Análisis de **JS/TS** orquestando ESLint como subproceso y normalizando su salida.
- Reglas de seguridad básicas (SAST): secretos hardcodeados, inyección SQL evidente.
- Métricas básicas: LOC, complejidad ciclomática.
- Salidas: tabla en consola, JSON propio, HTML standalone, SARIF.
- Reporte por ejecución, sin estado ni histórico entre corridas.

Fuera de alcance: persistencia, histórico, quality gates, comparación entre ejecuciones, servicio web/UI.

## Instalación / build

Requiere .NET SDK (`net10.0`).

```bash
dotnet build
```

Para publicar el binario self-contained (Native AOT):

```bash
dotnet publish src/Koduma.Cli/Koduma.Cli.csproj -c Release -r <RID> --self-contained -o <carpeta-destino>
```

donde `<RID>` es `linux-x64`, `win-x64` o `osx-x64`/`osx-arm64` según la plataforma.

## Uso

### CLI

```bash
koduma analyze ./repo                          # tabla en consola, exit code según issues
koduma analyze ./repo --format json,html,sarif  # uno o más formatos a la vez
koduma analyze ./repo --fail-on major           # umbral de severidad para exit code 1 (default: critical)
koduma rules                                    # lista las reglas disponibles
```

Códigos de salida: `0` sin issues por encima del umbral, `1` hay issues de severidad igual o superior a `--fail-on`, `2` error de ejecución o configuración.

### Servidor MCP

```bash
koduma mcp-serve --root ./repo
```

Expone por stdio:
- `analyze_project(path, format?, rules_config?)` — ejecuta el análisis completo.
- `list_rules(language?)` — lista las reglas disponibles.
- `get_capabilities()` — lenguajes y categorías soportadas.

Cada llamada es autocontenida; no hay estado entre llamadas.

## Configuración (`koduma.json`)

Opcional, en la raíz del repo analizado:

```json
{
  "rules": {
    "CS-NAMING-001": "off",
    "SEC-SECRET-001": { "severity": "blocker" }
  },
  "exclude": ["**/bin/**", "**/obj/**", "**/node_modules/**"]
}
```

Si no existe, se usa un set de reglas por defecto por lenguaje. El discovery de archivos respeta además `.gitignore` y un `.kodumaignore` opcional.

## Reglas de C#

Reglas propias (Roslyn, in-proc, sin necesidad de SDK):

| Categoría | Reglas |
|---|---|
| Bugs | `CS-BUG-001` async void · `CS-BUG-002` bloqueo síncrono de tareas en métodos async · `CS-BUG-003` `throw ex;` · `CS-BUG-004` lock sobre `this`/`typeof`/string · `CS-BUG-005` operandos idénticos · `CS-BUG-006` cuerpo vacío (`if (x);`) |
| Diseño / smells | `CS-DESIGN-001` catch vacío · `-002` demasiados parámetros · `-003` método largo · `-004` catch de `Exception` sin relanzar · `-005` tipo de más de 500 líneas · `-006` anidamiento > 4 · `CS-NAMING-001/002` · `CS-COMPLEXITY-001` · `CS-SMELL-001` TODO/FIXME · `CS-SMELL-002` campo estático público mutable |
| Seguridad | `CS-SEC-001` criptografía débil (MD5/SHA1/DES/3DES/RC2) · `CS-SEC-002` deserialización insegura (`BinaryFormatter`, `TypeNameHandling`) · `CS-SEC-003` validación de certificado TLS desactivada |

`koduma rules` las lista; cada una se puede desactivar o cambiar de severidad en `koduma.json`.

### Analizadores de .NET (`NET-*`)

`DotnetBuildAnalyzer` compila la solución (o, si no hay `.sln`/`.slnx` en la raíz, el proyecto más cercano a cada archivo) con `dotnet build` y lee el log SARIF que emite Roslyn. Así se obtienen los analizadores oficiales de .NET (`Microsoft.CodeAnalysis.NetAnalyzers`, `AnalysisMode=Recommended`) más cualquier analizador NuGet del propio repo y los avisos/errores del compilador. Los ids salen como `NET-CA2000`, `NET-CS8602`, etc., y se configuran en `koduma.json` igual que el resto (`"NET-CA1822": "off"`).

- Requiere el SDK de .NET en el `PATH`; si falta, se registra como error de lenguaje `dotnet` y el resto del análisis continúa.
- Hace un build real del repo analizado (escribe `bin/`/`obj/` y ejecuta su lógica MSBuild), igual que ESLint ejecuta su configuración. No uses `koduma analyze` sobre repos no confiables.
- `KODUMA_SKIP_DOTNET_BUILD=1` desactiva este analizador.
- Los hallazgos de .NET que Koduma ya cubre con regla propia (p. ej. `CA1031`, `CA2200`, `CA5350/51`) se omiten para no duplicar.
- Su catálogo depende del SDK instalado, por lo que `koduma rules` no lo enumera.

### Vulnerabilidades de dependencias (`VULN-*`)

`NuGetVulnerabilityAnalyzer` ejecuta `dotnet list package --vulnerable --include-transitive --format json` sobre la solución/proyectos analizados, y `NpmAuditAnalyzer` ejecuta `npm audit --json` en cada directorio con `package-lock.json`/`npm-shrinkwrap.json`. Los hallazgos usan ids `VULN-{GHSA-id}` (o `VULN-NUGET-*`/`VULN-NPM-*` si el aviso no trae un id de GHSA) y se configuran en `koduma.json` igual que cualquier otra regla (`"VULN-GHSA-5crp-9r3c-p9vr": "off"`).

- `KODUMA_SKIP_NUGET_AUDIT=1` / `KODUMA_SKIP_NPM_AUDIT=1` desactivan cada analizador.
- A diferencia del resto de analizadores, estos consultan un feed de avisos de terceros (el mirror de GitHub Advisory Database que usa NuGet, y el registro de npm) como parte normal de cada análisis, no solo al instalar la herramienta.

## Manejo de errores

Si un plugin de lenguaje falla (p. ej. ESLint no instalado), ese lenguaje se marca como error en el resultado pero el análisis de los demás lenguajes continúa (degradación parcial). Un `koduma.json` inválido aborta antes de empezar.

## Estructura

```
src/
├── Koduma.Core/               # Dominio + orquestación
├── Koduma.Analyzers.CSharp/   # Plugin Roslyn (in-proc)
├── Koduma.Analyzers.External/ # Plugin genérico: linters externos (ESLint, dotnet build + analizadores de .NET)
├── Koduma.Analyzers.Secrets/  # Reglas de seguridad (SAST)
├── Koduma.Reporting/          # Generadores: Table, Json, Html, Sarif
├── Koduma.Cli/                # Entry point (analyze / rules / mcp-serve)
└── Koduma.Mcp/                # Capa MCP sobre Koduma.Core
tests/
└── ...                        # un proyecto de test por componente, + Koduma.Integration.Tests (end-to-end)
```

## Tests

```bash
dotnet test
```

Documentación de diseño completa (specs y planes) en `docs/superpowers/`.
