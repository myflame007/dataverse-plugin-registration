# Roadmap — Dataverse.PluginRegistration

> Ziel: Vom funktionierenden Prototypen zum produktionsreifen dotnet-Tool
> fur die gesamte Dynamics 365 Community.

---

## Hinweis: .NET-Version & Dynamics 365 Kompatibilität

Dynamics 365 Plugins laufen serverseitig auf **.NET Framework 4.6.2** (on-prem) bzw. in der **Sandbox auf .NET 4.6.2/4.7.2** (online). Das betrifft aber **nur den Plugin-Code selbst** — also die DLLs, die in Dataverse deployt werden.

Unser Tool ist ein **externes CLI-Tool**, das über die Dataverse Web API / SDK kommuniziert. Es läuft auf der Entwickler-Maschine, nicht im Dataverse-Server. Deshalb:

- **Unser Tool ist komplett losgelöst** von der alten .NET Framework Version die Dynamics serverseitig nutzt
- **MetadataLoadContext** liest die Plugin-DLLs nur per Reflection, ohne sie auszuführen — die Target-Framework-Version der DLL ist irrelevant
- **Dataverse SDK (`Microsoft.PowerPlatform.Dataverse.Client`)** unterstützt .NET 6/8/10 nativ
- **Einzige Einschränkung:** Wenn wir `CrmPluginRegistrationAttribute` parsen, müssen wir die Attribut-Konstruktoren exakt so abbilden, wie sie im `Microsoft.CrmSdk.CoreAssemblies` NuGet definiert sind. Das tun wir bereits korrekt.

### Entscheidung: Direkt auf .NET 10 (LTS)

.NET 10 ist seit November 2025 als **LTS-Release** verfügbar (Support bis November 2028).
Da wir ohnehin ein Architektur-Refactoring machen, upgraden wir direkt auf .NET 10 statt
auf dem bald auslaufenden .NET 8 zu bleiben. Das gibt uns Zugang zu C# 14 Features,
die in unserer Codebase konkrete Verbesserungen bringen:

#### Konkrete .NET 10 / C# 14 Anwendungsfälle in unserem Projekt

**1. `field` Keyword (Semi-Auto Properties)**
Für unsere Config-Models (`PluginRegConfig`, `EnvironmentConfig`, `AssemblyConfig`) — Validierung direkt im Property-Setter ohne manuelles Backing Field:
```csharp
// Vorher (C# 12): manuelles Backing Field nötig
private string _url = "";
public string Url
{
    get => _url;
    set => _url = value ?? throw new ArgumentNullException(nameof(Url));
}

// Nachher (C# 14): field keyword
public string Url
{
    get => field;
    set => field = value ?? throw new ArgumentNullException(nameof(Url));
} = "";
```
**Wo:** `PluginRegConfig.cs`, `PluginMetadata.cs` — überall wo wir Properties mit Validierung brauchen.

**2. Extension Members (Extension Types)**
Statt statischer Utility-Methoden können wir Extension Properties und statische Extension-Methoden verwenden. Besonders nützlich für unsere Entity-Helpers:
```csharp
// Vorher: statische Helper-Klasse
public static class EntityExtensions
{
    public static bool HasChanged(Entity entity, string attr, object expected) { ... }
}

// Nachher: Extension Type mit Properties
extension(Entity entity)
{
    public bool IsNew => entity.Id == Guid.Empty;
    public bool HasChanged(string attr, object expected) => ...;
}
```
**Wo:** `StepRegistrar.cs` — Change Detection, Entity-Vergleiche.

**3. `params ReadOnlySpan<T>` (Zero-Allocation Params)**
Für unsere Query-Builder und Logging-Aufrufe — keine Array-Allokation mehr bei variablen Argumenten:
```csharp
// Vorher: alloziert implizit ein string[]
void Log(params string[] messages) { ... }

// Nachher: zero-allocation
void Log(params ReadOnlySpan<string> messages) { ... }
```
**Wo:** Logger-Aufrufe in `StepRegistrar.cs` und `PackageDeployer.cs` — bei 100+ Steps pro Deployment summieren sich die Allokationen.

**4. `Task.WhenEach` (Async Enumeration)**
Für parallele Dataverse-Operationen — z.B. wenn wir mehrere Steps gleichzeitig registrieren wollen:
```csharp
// Vorher: WhenAll wartet auf ALLE, dann verarbeitet alle auf einmal
var tasks = steps.Select(s => RegisterStepAsync(s));
var results = await Task.WhenAll(tasks);

// Nachher: WhenEach verarbeitet jeden sofort wenn er fertig ist
await foreach (var task in Task.WhenEach(steps.Select(s => RegisterStepAsync(s))))
{
    var result = await task;
    LogProgress(result); // sofortiges Feedback statt am Ende alles auf einmal
}
```
**Wo:** `StepRegistrar.RegisterSteps()` — für parallele Step-Registrierung mit Live-Progress.

**5. `System.Threading.Lock` (Typed Lock)**
Für unser Message/Filter-Caching in `StepRegistrar` — typsicherer als `object`-Lock:
```csharp
// Vorher
private readonly object _cacheLock = new();
lock (_cacheLock) { ... }

// Nachher: typsicher, bessere Fehlermeldungen
private readonly Lock _cacheLock = new();
lock (_cacheLock) { ... }
```
**Wo:** `StepRegistrar.cs` — Message-Cache und Filter-Cache sind aktuell nicht thread-safe.

**6. Null-Conditional Assignment (`?.=`)**
Für Config-Resolution und Default-Werte:
```csharp
// Vorher
if (config.Url != null) config.Url = EnvFile.Resolve(config.Url, env);

// Nachher
config.Url ?.= EnvFile.Resolve(config.Url, env);
```
**Wo:** `EnvFile.ResolveConfig()` — hat 8+ solcher Zuweisungen.

**7. Verbesserte `System.Text.Json` Source Generators**
Schnellere Serialisierung unserer Config-Dateien:
```csharp
[JsonSerializable(typeof(PluginRegConfig))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
partial class PluginRegJsonContext : JsonSerializerContext { }
```
**Wo:** `PluginRegConfig.cs` — Config-Laden und -Speichern wird schneller und AOT-kompatibel.

#### Upgrade-Schritte (in Phase 0 integriert)

- [ ] `TargetFramework` in `.csproj` von `net8.0` auf `net10.0` ändern
- [ ] `LangVersion` auf `14` setzen (oder `preview` für neueste Features)
- [ ] .NET 10 SDK installieren (falls noch nicht vorhanden)
- [ ] NuGet-Pakete auf .NET 10 kompatible Versionen updaten
- [ ] `dotnet build` verifizieren — keine Breaking Changes erwartet
- [ ] Schrittweise C# 14 Features einführen (nicht alles auf einmal, sondern bei jedem Refactoring-Schritt)

---

## Phase 0 — Architektur-Refactoring (Testbarkeit & Wartbarkeit)

Bevor wir Features hinzufügen oder Tests schreiben, muss die Codebasis so umgebaut werden,
dass sie **testbar und erweiterbar** ist. Aktuell sind die Klassen teilweise eng gekoppelt
und schwer isoliert testbar.

### 0.0 Agent-Reviews fur Phase 0

> Nach Abschluss der Architektur-Arbeit lassen wir spezialisierte Agents die Ergebnisse reviewen.

- [ ] **Software Architect** Agent: DI-Setup, Interface-Design und Projektstruktur reviewen
  - Pruft: Sind die Abstractions sinnvoll? Stimmen die Lifetimes (Singleton/Transient/Scoped)?
  - Pruft: Ist die Ordnerstruktur skalierbar? Gibt es zirkulare Abhangigkeiten?
- [ ] **Code Reviewer** Agent: Gesamtes Refactoring reviewen
  - Pruft: Korrektheit, keine Regressions, saubere Interfaces
  - Pruft: Werden C# 14 Features sinnvoll eingesetzt oder erzwungen?
- [ ] **Security Engineer** Agent: Auth-Refactoring reviewen
  - Pruft: IAuthProvider-Abstraktion leakt keine Credentials
  - Pruft: ServiceClient-Disposal korrekt, keine Token in Logs
- [ ] `/simplify` Skill auf alle refactored Files anwenden

### 0.1 Sofort-Fixes (vor dem Refactoring)

Dinge die jetzt schon falsch sind und vor dem Architektur-Umbau gefixt werden mussen,
weil sie sonst ins neue Design ubernommen werden.

- [ ] **`.env` in `.gitignore` aufnehmen** — aktuell fehlt der Eintrag, Credential-Leak-Risiko!
- [x] ~~**Hardcoded `"ava"` Publisher-Prefix entfernen**~~ (DONE)
  - `AssemblyConfig.PublisherPrefix` Default auf `""` geandert
  - `ResolveArgs` Fallback auf `""` geandert
  - Fur ein Open-Source-Tool darf kein firmenspezifischer Default existieren
- [ ] **`Profile` Property in `AssemblyConfig` klaren**
  - Aktuell existiert die Property mit Default `"debug"`, wird aber nirgends gelesen
  - Entweder implementieren (verschiedene DLL-Pfade fur debug/release) oder entfernen
  - Halbimplementierte Features verwirren User
- [ ] **Config-Schema-Version einfuhren**
  - `"schemaVersion": 1` in `pluginreg.json` aufnehmen
  - Bei jedem Laden prufen: bekannte Version? Wenn nicht → klare Fehlermeldung
  - Ohne das konnen zukunftige Config-Anderungen silent falsche Ergebnisse liefern

**Definition of Done:**
- [ ] `.env` steht in `.gitignore`
- [ ] Kein hardcoded `"ava"` mehr im gesamten Projekt
- [ ] `Profile` ist entweder implementiert oder entfernt
- [ ] `pluginreg.json` hat ein `schemaVersion` Feld das beim Laden validiert wird

### 0.2 Interfaces extrahieren

Aktuell werden `StepRegistrar`, `PackageDeployer` und `DataverseAuth` direkt instanziiert.
Fur Testbarkeit brauchen wir Interfaces, damit wir in Tests Mocks einsetzen konnen.

- [ ] `IStepRegistrar` Interface extrahieren
  - Methode: `void RegisterSteps(string assemblyName, List<PluginStepInfo> steps)`
  - Methode: `void DeleteOrphanedSteps(string assemblyName, List<PluginStepInfo> currentSteps)` (für Phase 2)
- [ ] `IPackageDeployer` Interface extrahieren
  - Methode: `Guid Push(string nupkgPath, string packageName, string publisherPrefix, string? solutionName)`
- [ ] `IAttributeReader` Interface extrahieren (aktuell statisch)
  - Methode: `List<PluginStepInfo> ReadFromAssembly(string assemblyPath)`
  - Statische Klasse zu instanziierbarer Klasse umbauen
- [ ] `IAuthProvider` Interface extrahieren
  - Methode: `Task<IOrganizationService> ConnectAsync(EnvironmentConfig config, CancellationToken ct)`
  - Rückgabetyp auf `IOrganizationService` statt `ServiceClient` ändern — ermöglicht Mocking
- [ ] `IFileSystem` Interface einführen (für PackageDeployer)
  - Methoden: `byte[] ReadAllBytes(string path)`, `bool FileExists(string path)`, `Stream OpenZip(string path)`
  - Default-Implementierung `PhysicalFileSystem` delegiert an `System.IO`
  - In Tests: `FakeFileSystem` mit In-Memory-Daten

**Definition of Done:**
- [ ] Jede Klasse hat ein zugehöriges Interface im Ordner `/Interfaces/`
- [ ] Alle Klassen implementieren ihr Interface
- [ ] Kein `new StepRegistrar(...)` mehr in Program.cs — nur noch über Interface
- [ ] Code kompiliert und `register`-Befehl funktioniert weiterhin identisch

### 0.3 Dependency Injection einführen

Aktuell wird alles in `Program.cs` manuell verdrahtet. Für Testbarkeit und Erweiterbarkeit
brauchen wir einen DI-Container.

- [ ] `Microsoft.Extensions.DependencyInjection` NuGet hinzufügen
- [ ] `ServiceCollection` in Program.cs aufsetzen
- [ ] Registrierungen:
  - [ ] `IAuthProvider` → `DataverseAuth` (Singleton)
  - [ ] `IOrganizationService` → via Factory aus `IAuthProvider` (Scoped)
  - [ ] `IPackageDeployer` → `PackageDeployer` (Transient)
  - [ ] `IStepRegistrar` → `StepRegistrar` (Transient)
  - [ ] `IAttributeReader` → `AttributeReader` (Singleton)
  - [ ] `IFileSystem` → `PhysicalFileSystem` (Singleton)
  - [ ] `ILogger` → Console-Logger (Singleton)
- [ ] Command-Handler als eigene Klassen auslagern (aus Program.cs raus):
  - [ ] `InitCommand.cs` — Erstellt pluginreg.json
  - [ ] `RegisterCommand.cs` — Orchestriert Deploy + Register
  - [ ] `ListCommand.cs` — Zeigt Steps ohne Dataverse-Verbindung
- [ ] Program.cs wird schlank: nur noch Argument-Parsing → DI Setup → Command-Dispatch

**Definition of Done:**
- [ ] Program.cs hat < 80 Zeilen
- [ ] Jeder Command ist eine eigene Klasse mit `Execute()`-Methode
- [ ] Alle Dependencies werden über Konstruktor-Injektion aufgelöst
- [ ] Keine `static` Business-Logik mehr (außer reine Utility-Funktionen wie EnvFile)
- [ ] Code kompiliert und alle 3 Befehle (`init`, `register`, `list`) funktionieren identisch

### 0.4 Projektstruktur reorganisieren

Aktuell liegen alle .cs-Dateien flach im Root. Für Wartbarkeit und Orientierung brauchen
wir eine klare Ordnerstruktur.

- [ ] Ordnerstruktur anlegen:
  ```
  /src/
    /Commands/          ← InitCommand, RegisterCommand, ListCommand
    /Services/          ← StepRegistrar, PackageDeployer, DataverseAuth
    /Models/            ← PluginStepInfo, PluginRegConfig, AssemblyConfig, EnvironmentConfig
    /Interfaces/        ← IStepRegistrar, IPackageDeployer, IAuthProvider, IAttributeReader, IFileSystem
    /Infrastructure/    ← EnvFile, PhysicalFileSystem
    Program.cs          ← Einstiegspunkt (schlank)
  /tests/
    /Unit/              ← AttributeReaderTests, StepRegistrarTests, etc.
    /Integration/       ← Dataverse-Integrationstests (brauchen echte Umgebung)
  ```
- [ ] `.csproj` Pfade anpassen
- [ ] Test-Projekt anlegen: `Dataverse.PluginRegistration.Tests.csproj`
  - xUnit als Test-Framework
  - Moq oder NSubstitute für Mocking
  - FluentAssertions für lesbare Assertions
- [ ] Solution-File (`.sln`) anlegen, das beide Projekte enthält

**Definition of Done:**
- [ ] `dotnet build` erfolgreich für beide Projekte
- [ ] `dotnet test` läuft (auch wenn noch keine Tests existieren)
- [ ] Jede Datei liegt im richtigen Ordner laut Schema oben
- [ ] Solution-File existiert und beide Projekte sind referenziert

### 0.5 Logging vereinheitlichen

Aktuell wird `Action<string> log` als Callback durch die Klassen gereicht.
Das funktioniert, ist aber schwer erweiterbar (kein Log-Level, kein File-Output).

- [ ] `Microsoft.Extensions.Logging` NuGet hinzufügen
- [ ] `ILogger<T>` statt `Action<string>` in allen Klassen verwenden
- [ ] Log-Levels einführen:
  - `LogDebug` — Detaillierte Infos (Query-Details, Entity-Attribute)
  - `LogInformation` — Normaler Ablauf (Step registriert, Package deployed)
  - `LogWarning` — Verdächtiges Verhalten (Step unverändert, Package-Version gleich)
  - `LogError` — Fehler mit Recovery-Möglichkeit
  - `LogCritical` — Fatale Fehler (Auth fehlgeschlagen, Config ungültig)
- [ ] `--verbose` Flag einführen → setzt MinimumLevel auf Debug
- [ ] Optional: File-Logging in `plugin-reg.log` für Diagnose

**Definition of Done:**
- [ ] Alle `Console.WriteLine`-Aufrufe in Services durch `ILogger` ersetzt
- [ ] `--verbose` zeigt Debug-Output, ohne `--verbose` nur Information+
- [ ] Fehlermeldungen enthalten Kontext (welcher Step, welche Assembly, welche Umgebung)
- [ ] Kein `Action<string> log` Callback mehr in Konstruktoren

---

## Phase 1 — Kritische Bugs & fehlende Logik fixen

### 1.0 Agent-Reviews fur Phase 1

- [ ] **Code Reviewer** Agent: Jeden Bugfix einzeln reviewen (nach 1.1, 1.2, 1.3)
  - Pruft: Fix ist korrekt und vollstandig, keine neuen Bugs eingefuhrt
  - Pruft: Change Detection Logik ist konsistent
- [ ] **Security Engineer** Agent: SecureConfiguration-Implementierung reviewen (nach 1.3)
  - Pruft: Secure Config wird nie in Logs geschrieben
  - Pruft: Keine unverschlusselte Speicherung von Secrets
- [ ] `/simplify` Skill auf geanderte Files anwenden

### 1.1 IsolationMode tatsächlich setzen

**Bug:** `AttributeReader` parst den IsolationMode korrekt aus dem Attribut, aber
`StepRegistrar.UpsertStep()` setzt ihn nie auf dem `SdkMessageProcessingStep`-Entity.
Alle Steps werden mit dem Default-IsolationMode registriert.

- [ ] In `UpsertStep()`: `step["isolationmode"] = new OptionSetValue(stepInfo.IsolationMode)` setzen
- [ ] In `StepHasChanges()`: IsolationMode in den Vergleich aufnehmen
- [ ] Unit Test: Step mit IsolationMode=Sandbox → Entity hat korrekten OptionSetValue
- [ ] Unit Test: Step mit IsolationMode=None → Entity hat korrekten OptionSetValue
- [ ] Unit Test: Änderung von Sandbox→None wird als Change erkannt

**Definition of Done:**
- [ ] IsolationMode wird bei Create und Update korrekt gesetzt
- [ ] Change Detection erkennt IsolationMode-Änderungen
- [ ] 3 Unit Tests grün

### 1.2 Bare `catch {}` Blöcke eliminieren

**Bug:** In `StepRegistrar.cs` gibt es einen `catch {}` Block, der **alle** Exceptions
stillschweigend verschluckt. Wenn die PluginPackage-Query fehlschlägt (z.B. wegen
fehlender Berechtigung), sieht der User nichts.

- [ ] `catch {}` ersetzen durch `catch (Exception ex)` mit Logger-Aufruf
- [ ] Spezifische Exception-Typen fangen wo möglich:
  - `FaultException<OrganizationServiceFault>` für Dataverse-spezifische Fehler
  - Wenn Entity nicht existiert → `LogWarning("PluginPackage entity not available — using PluginAssembly fallback")`
  - Wenn anderer Fehler → `LogError(ex, "Failed to query PluginPackage")` und weiterwerfen
- [ ] Review: Gibt es weitere unterdrückte Exceptions? (Codebase durchsuchen)

**Definition of Done:**
- [ ] Kein einziger `catch { }` oder `catch (Exception) { }` ohne Logging im gesamten Projekt
- [ ] PluginPackage-Fallback wird im Log klar kommuniziert
- [ ] Echte Fehler (Berechtigung, Netzwerk) werden nicht mehr verschluckt

### 1.3 Custom API Registrierung reparieren

**Bug:** `AttributeReader` parst den 1-Argument-Constructor (Custom APIs) und erstellt ein
`PluginStepInfo` mit nur einem Message-Namen. Aber `StepRegistrar` behandelt das wie einen
normalen `SdkMessageProcessingStep` — das ist **falsch**. Custom APIs mussen uber die Entities
`customapi`, `customapirequestparameter` und `customapiresponseproperty` registriert werden.

Aktuell erzeugt der Code stille Fehler oder kaputte Registrierungen fur Custom API Plugins.

- [ ] Custom API Detection: Wenn `PluginStepInfo` nur Message hat (kein Entity, kein Stage) → Custom API Pfad
- [ ] Eigenen `CustomApiRegistrar` erstellen (oder in `StepRegistrar` separaten Pfad)
- [ ] Custom API Entity erstellen/updaten: `customapi` mit `uniquename`, `bindingtype`, `plugintypeid`
- [ ] Request/Response Parameter registrieren (falls im Attribut definiert)
- [ ] Alternativ: Custom APIs explizit als "nicht unterstutzt" markieren und mit klarer Warnung uberspringen
  - `WARNING: Custom API 'my_CustomAction' detected but Custom API registration is not yet supported. Skipping.`
- [ ] Unit Test: Custom API wird korrekt erkannt und behandelt (registriert oder gewarnt)

**Definition of Done:**
- [ ] Custom APIs werden entweder korrekt registriert ODER mit klarer Warnung ubersprungen
- [ ] Keine stillen Fehler oder kaputten Registrierungen mehr
- [ ] 1+ Unit Tests grun

### 1.4 Solution-aware Step Registration

**Bug:** `PackageDeployer.Push()` fugt das Package korrekt zur Solution hinzu via
`SolutionUniqueName` auf dem `CreateRequest`. Aber `StepRegistrar.UpsertStep()` fugt
neu erstellte Steps **nicht** zur Solution hinzu. In Dataverse werden Steps nicht automatisch
in eine Solution aufgenommen nur weil das Parent-Package drin ist.

Konsequenz: Beim Solution-Export fehlen alle Steps. Andere Umgebungen bekommen das Package
aber keine Step-Registrierungen.

- [ ] Bei `Create` eines Steps: `SolutionUniqueName` im `CreateRequest` setzen (wie bei Package)
- [ ] Solution-Name aus `AssemblyConfig.SolutionName` durchreichen
- [ ] Unit Test: Neuer Step wird mit Solution-Name erstellt
- [ ] Unit Test: Ohne Solution-Name funktioniert Create trotzdem (optional field)

**Definition of Done:**
- [ ] Neue Steps werden automatisch der konfigurierten Solution zugewiesen
- [ ] Solution-Export enthalt alle registrierten Steps
- [ ] 2 Unit Tests grun

### 1.5 Multi-Assembly Iteration implementieren

**Bug:** `Program.cs` `ResolveArgs()` greift immer auf `config.Assemblies[0]` zu.
Die Config unterstutzt mehrere Assemblies, aber der Code deployt nur die erste.
Phase 4.5 testet Multi-Assembly, aber es ist nirgends implementiert.

- [ ] `RegisterCommand` muss uber alle `config.Assemblies` iterieren
- [ ] Jede Assembly einzeln deployen (Package + Steps)
- [ ] Output klar nach Assembly gruppieren:
  ```
  [1/3] Assembly: MyPlugin.Core
    Package: MyPlugin.Core v1.2.0 (uploaded)
    Steps: 4 created, 2 unchanged

  [2/3] Assembly: MyPlugin.Extensions
    Package: MyPlugin.Extensions v1.0.0 (unchanged)
    Steps: 1 updated
  ```
- [ ] Fehler in einer Assembly soll die anderen nicht blockieren (Fehler sammeln, am Ende alle ausgeben)
- [ ] Unit Test: 2 Assemblies → beide werden verarbeitet

**Definition of Done:**
- [ ] Alle konfigurierten Assemblies werden deployt, nicht nur die erste
- [ ] Output ist nach Assembly gruppiert
- [ ] Fehler in Assembly A blockiert Assembly B nicht
- [ ] 1+ Unit Tests grun

### 1.6 `pluginpackageid` vs `packageid` Feldname verifizieren

**Bug-Verdacht:** `StepRegistrar.FindPluginTypes()` filtert auf `pluginpackageid`.
Im Dataverse-Schema heisst das Lookup-Feld auf `plugintype` fur NuGet-Packages
moglicherweise `packageid` statt `pluginpackageid`. Dieser Fehler ist aktuell durch
den bare `catch {}` Block (1.2) maskiert — wenn wir den catch entfernen, konnte das
als Laufzeitfehler auftauchen.

- [ ] Dataverse-Schema prufen: Wie heisst das Feld tatsachlich?
  - In Test-Umgebung: `GET /api/data/v9.2/EntityDefinitions(LogicalName='plugintype')/Attributes`
  - Oder: Plugin Registration Tool → PluginType Entity → Attributes ansehen
- [ ] Feldnamen korrigieren falls falsch
- [ ] Integration Test: PluginType-Query liefert Ergebnisse fur ein bekanntes Package

**Definition of Done:**
- [ ] Korrekter Feldname verifiziert und im Code verwendet
- [ ] FindPluginTypes liefert Ergebnisse fur NuGet-basierte Packages

### 1.7 SecureConfiguration implementieren

**Bug:** `AttributeReader` parst `SecureConfiguration` aus dem Attribut, aber
`StepRegistrar` ignoriert den Wert. Secure Configs werden nie an Dataverse übergeben.

- [ ] `SecureConfiguration` in `UpsertStep()` setzen → `sdkmessageprocessingstepsecureconfig` Entity
  - Secure Config ist ein **separater** Entity-Record, kein Attribut auf dem Step
  - Muss als eigener Record erstellt und am Step verlinkt werden
- [ ] Bei Update: bestehende SecureConfig updaten statt neue erstellen
- [ ] Unit Test: Step mit SecureConfiguration → separater Entity wird erstellt
- [ ] Unit Test: SecureConfig-Änderung wird erkannt und aktualisiert

**Definition of Done:**
- [ ] SecureConfiguration wird korrekt als eigener Entity erstellt/aktualisiert
- [ ] Change Detection funktioniert für SecureConfiguration
- [ ] 2 Unit Tests grün

---

## Phase 2 — Fehlende Kern-Features

### 2.0 Agent-Reviews fur Phase 2

- [ ] **Backend Architect** Agent: Retry-Decorator und async Patterns reviewen (nach 2.3)
  - Pruft: Polly-Policy korrekt konfiguriert, Backoff-Werte sinnvoll
  - Pruft: Decorator-Pattern sauber implementiert, keine Seiteneffekte
- [ ] **Code Reviewer** Agent: Jedes Feature einzeln reviewen (nach 2.1, 2.2, 2.3, 2.4)
  - Pruft: Dry-Run hat wirklich keine Seiteneffekte
  - Pruft: Orphan Cleanup loscht nicht versehentlich aktive Steps
- [ ] **Security Engineer** Agent: Orphan-Deletion reviewen (nach 2.2)
  - Pruft: Keine unbeabsichtigte Datenloschung ohne Bestatigung
  - Pruft: `--force` Flag kann nicht versehentlich ausgelost werden
- [ ] `/simplify` Skill auf alle neuen Features anwenden

### 2.1 Token Refresh bei lang laufenden Operationen

**Problem:** `DataverseAuth.ConnectAsync()` holt einmalig ein Token via MSAL und gibt es
als statischen Wert an den `ServiceClient` weiter: `tokenProviderFunction: async _ => authResult.AccessToken`.
MSAL Access Tokens laufen nach 60-75 Minuten ab. Bei grossen Deployments (100+ Steps)
kann die Operation langer dauern. Danach schlagen alle Dataverse-Calls mit 401 fehl —
und die Retry-Logik (2.4) hilft nicht, weil jeder Retry dasselbe abgelaufene Token verwendet.

- [ ] Token Provider andern: `AcquireTokenSilent` zuerst (cached Refresh Token), Fallback auf Interactive
- [ ] Token-Ablauf uberwachen: Wenn Token in < 5 Minuten ablauft, proaktiv refreshen
- [ ] Logging: `"Access token refreshed (was expiring in 3 min)"`
- [ ] Unit Test: Token Provider wird erneut aufgerufen wenn Token abgelaufen

**Definition of Done:**
- [ ] Token wird automatisch refreshed bevor er ablauft
- [ ] Kein 401-Fehler bei Operationen > 60 Minuten
- [ ] 1+ Unit Tests grun

### 2.2 Non-Interactive Auth fur CI/CD

**Problem:** `DataverseAuth` unterstutzt nur interaktives OAuth (Browser-basiert) oder
rohe Connection Strings. Fur CI/CD Pipelines (Phase 7) brauchen wir headless Authentication:
Client ID + Client Secret, oder Client ID + Certificate. Ein Build-Agent kann keinen Browser offnen.

- [ ] `authType` in `EnvironmentConfig` erweitern:
  - `"OAuth"` — bestehend, interaktiver Browser-Flow
  - `"ClientSecret"` — Client ID + Secret (fur CI/CD)
  - `"Certificate"` — Client ID + Certificate Thumbprint (fur Enterprise)
- [ ] `EnvironmentConfig` um Felder erweitern: `clientSecret`, `certificateThumbprint`
- [ ] `DataverseAuth.ConnectAsync()` je nach `authType` verschiedene MSAL-Flows verwenden:
  - `AcquireTokenByClientCredential` fur ClientSecret
  - `AcquireTokenByClientCertificate` fur Certificate
- [ ] Secrets aus `.env` / Umgebungsvariablen laden (nie hardcoded in Config)
- [ ] Unit Test: ClientSecret-Flow erzeugt korrekten Connection String
- [ ] Dokumentation: Wie man eine App Registration fur CI/CD erstellt

**Definition of Done:**
- [ ] `plugin-reg register` funktioniert headless in einer CI/CD Pipeline
- [ ] Client Secret und Certificate Auth werden unterstutzt
- [ ] Secrets kommen aus Umgebungsvariablen, nicht aus der Config-Datei
- [ ] 1+ Unit Tests grun
- [ ] README-Abschnitt fur CI/CD Setup

### 2.3 PluginType-Verfugbarkeit nach Package-Upload

**Problem:** Nach `PackageDeployer.Push()` entdeckt Dataverse automatisch PluginTypes
aus der hochgeladenen Assembly. Aber das passiert asynchron — `StepRegistrar.FindPluginTypes()`
wird sofort danach aufgerufen und findet moglicherweise noch keine PluginTypes.

- [ ] Nach Package-Upload: Polling auf PluginTypes mit Timeout
  - Max 30 Sekunden warten, alle 2 Sekunden prufen
  - `"Waiting for Dataverse to discover plugin types... (attempt 3/15)"`
- [ ] Wenn Timeout: Klare Fehlermeldung statt kryptischem "No plugin types found"
- [ ] Bei bestehendem Package (Update, nicht Create): Polling uberspringen (Types existieren schon)
- [ ] Unit Test: Polling wird nur bei neuem Package ausgefuhrt

**Definition of Done:**
- [ ] Nach Package-Upload wird auf PluginType-Erkennung gewartet
- [ ] Timeout nach 30s mit klarer Fehlermeldung
- [ ] Bei Package-Update kein unnotiges Warten
- [ ] 1+ Unit Tests grun

### 2.4 `--dry-run` Modus

User müssen vorab sehen können, was passieren wird, ohne tatsächlich etwas zu ändern.
Besonders kritisch für Produktionsumgebungen.

- [ ] `--dry-run` Flag in Argument-Parsing aufnehmen
- [ ] In `RegisterCommand`: wenn dry-run, dann:
  - [ ] Steps aus DLL lesen (wie gehabt)
  - [ ] Verbindung zu Dataverse herstellen (für Vergleich)
  - [ ] Bestehende Steps abfragen
  - [ ] Vergleich durchführen und **nur loggen**, nicht schreiben:
    - `[DRY-RUN] WOULD CREATE step: PreValidation of Create on account`
    - `[DRY-RUN] WOULD UPDATE step: PostOperation of Update on contact (changed: filteringattributes)`
    - `[DRY-RUN] WOULD DELETE orphaned step: PreOperation of Delete on lead`
    - `[DRY-RUN] WOULD SKIP step: PostOperation of Create on account (unchanged)`
  - [ ] Package-Upload simulieren:
    - `[DRY-RUN] WOULD UPLOAD package MyPlugin.1.2.0.nupkg (new)`
    - `[DRY-RUN] WOULD UPDATE package MyPlugin 1.1.0 → 1.2.0`
    - `[DRY-RUN] WOULD SKIP package MyPlugin (version 1.2.0 already deployed)`
- [ ] Exit-Code: 0 wenn keine Änderungen nötig, 1 wenn Änderungen ausstehen
- [ ] Unit Test: dry-run ruft nie `_svc.Create()` oder `_svc.Update()` auf

**Definition of Done:**
- [ ] `plugin-reg register --dry-run` zeigt alle geplanten Änderungen ohne Seiteneffekte
- [ ] Output unterscheidet klar zwischen CREATE / UPDATE / DELETE / SKIP
- [ ] Kein einziger Schreibzugriff auf Dataverse bei `--dry-run`
- [ ] Unit Test verifiziert, dass IOrganizationService.Create/Update nie aufgerufen wird

### 2.5 Orphaned Step Cleanup (Step-Löschung)

Wenn ein Step aus dem Plugin-Code entfernt wird, bleibt er aktuell als Leiche in Dataverse.
Das kann zu Laufzeitfehlern führen, wenn der Step-Code nicht mehr existiert.

- [ ] Nach der Registrierung: alle Steps in Dataverse abfragen, die zu dieser Assembly/Package gehören
- [ ] Vergleich mit den aktuell im Code definierten Steps
- [ ] Steps, die in Dataverse existieren aber nicht mehr im Code:
  - [ ] Im `--dry-run` Modus: nur loggen
  - [ ] Im normalen Modus: Bestätigungsprompt anzeigen:
    ```
    The following steps exist in Dataverse but not in your code:
      - PreOperation of Delete on account (step id: xxx)
      - PostOperation of Update on contact (step id: xxx)
    Delete these orphaned steps? [y/N]
    ```
  - [ ] Bei `--force` Flag: ohne Prompt löschen
  - [ ] Zugehörige Images ebenfalls löschen (Cascade)
- [ ] Unit Test: Orphaned Steps werden erkannt
- [ ] Unit Test: Löschung ruft `_svc.Delete()` mit korrekter Step-ID auf
- [ ] Unit Test: `--force` überspringt Prompt

**Definition of Done:**
- [ ] Orphaned Steps werden erkannt und aufgelistet
- [ ] Löschung nur nach Bestätigung (oder mit `--force`)
- [ ] Zugehörige Images werden mitgelöscht
- [ ] 3 Unit Tests grün
- [ ] Manueller Test: Step aus Plugin entfernen → `register` bietet Löschung an

### 2.6 Retry-Logik mit Exponential Backoff

Dataverse hat transiente Fehler (Timeouts, 429 Too Many Requests, 503 Service Unavailable).
Ein CLI-Tool, das beim ersten Timeout abbricht, ist in der Praxis unbrauchbar.

- [ ] `Polly` NuGet hinzufügen (Standard-Library für Retry in .NET)
- [ ] Retry-Policy definieren:
  - Max 3 Retries
  - Exponential Backoff: 1s → 2s → 4s
  - Retry bei: `TimeoutException`, `FaultException` mit ErrorCode `-2147204784` (Throttling), HTTP 429, HTTP 503
  - **Nicht** retrien bei: Auth-Fehler (401/403), ungültige Requests (400), Not Found (404)
- [ ] Policy um `IOrganizationService`-Aufrufe wrappen
  - Am besten als Decorator-Pattern: `RetryingOrganizationService : IOrganizationService`
  - Delegiert alle Calls an den echten Service, aber mit Retry-Wrapper
- [ ] Retry-Versuche loggen: `"Dataverse returned 429 — retrying in 2s (attempt 2/3)"`
- [ ] Unit Test: Transiente Fehler werden retried
- [ ] Unit Test: Permanente Fehler werden sofort geworfen

**Definition of Done:**
- [ ] Transiente Dataverse-Fehler werden automatisch retried (max 3x)
- [ ] Permanente Fehler werden sofort geworfen (kein sinnloses Warten)
- [ ] Jeder Retry wird im Log mit Wartezeit und Versuchsnummer geloggt
- [ ] 2 Unit Tests grün
- [ ] Decorator-Pattern: bestehender Code bleibt unverändert, Retry wird drumrum gewickelt

### 2.7 Package-Version-Check

Aktuell wird das NuGet-Package bei jedem `register`-Aufruf hochgeladen, auch wenn sich
die Version nicht geändert hat. Das ist unnötig und kostet Zeit.

- [ ] Vor dem Upload: existierendes PluginPackage in Dataverse abfragen
- [ ] Version aus Dataverse mit Version aus .nupkg vergleichen
- [ ] Wenn identisch: `"Package MyPlugin v1.2.0 already deployed — skipping upload"`
- [ ] Wenn unterschiedlich: Upload durchführen
- [ ] `--force-upload` Flag: Upload erzwingen auch bei gleicher Version
- [ ] Unit Test: gleiche Version → kein Upload
- [ ] Unit Test: neue Version → Upload
- [ ] Unit Test: `--force-upload` → Upload trotz gleicher Version

**Definition of Done:**
- [ ] Identische Package-Versionen werden nicht erneut hochgeladen
- [ ] Versionsprüfung wird im Log angezeigt
- [ ] `--force-upload` umgeht die Prüfung
- [ ] 3 Unit Tests grün

---

## Phase 3 — Test-Suite aufbauen

### 3.0 Agent-Reviews fur Phase 3

- [ ] **Test Results Analyzer** Agent: Test-Ergebnisse auswerten (nach 3.1-3.4)
  - Pruft: Coverage-Lucken identifizieren — welche Pfade sind nicht getestet?
  - Pruft: Sind die Test-Assertions aussagekraftig genug?
  - Pruft: Gibt es redundante Tests, die zusammengefasst werden konnen?
- [ ] **Code Reviewer** Agent: Test-Code reviewen
  - Pruft: Tests sind unabhangig voneinander (keine Test-Order-Abhangigkeit)
  - Pruft: Mocks sind korrekt konfiguriert, keine Over-Mocking
  - Pruft: Fixture-DLLs decken alle realen Szenarien ab

### 3.1 Unit Tests — AttributeReader

Der AttributeReader ist die testbarste Komponente (pure Functions, keine Dependencies).
Hier fangen wir an.

- [ ] Test-DLLs als Fixtures erstellen:
  - [ ] `TestPlugin.dll` mit verschiedenen `CrmPluginRegistrationAttribute`-Varianten:
    - Standard Plugin Step (8-Arg Constructor)
    - Custom API (1-Arg Constructor)
    - Workflow Activity (5-Arg Constructor)
    - Plugin mit Image1 und Image2
    - Plugin mit SecureConfiguration
    - Plugin mit FilteringAttributes
    - Plugin ohne Entity (globaler Step)
    - Mehrere Steps auf einer Klasse
    - Klasse ohne Attribut (soll ignoriert werden)
- [ ] Tests schreiben:
  - [ ] `ReadFromAssembly_StandardPlugin_ReturnsCorrectStep()`
  - [ ] `ReadFromAssembly_CustomApi_ReturnsCorrectStep()`
  - [ ] `ReadFromAssembly_WorkflowActivity_ReturnsNull()`
  - [ ] `ReadFromAssembly_WithImages_ParsesBothImages()`
  - [ ] `ReadFromAssembly_WithSecureConfig_ParsesConfig()`
  - [ ] `ReadFromAssembly_MultipleSteps_ReturnsAll()`
  - [ ] `ReadFromAssembly_NoAttributes_ReturnsEmptyList()`
  - [ ] `ReadFromAssembly_InvalidPath_ThrowsFileNotFound()`
  - [ ] `MapStage_AllValues_MapsCorrectly()` (Parameterized Test)
  - [ ] `MapExecMode_AllValues_MapsCorrectly()` (Parameterized Test)
  - [ ] `MapIsolation_AllValues_MapsCorrectly()` (Parameterized Test)

**Definition of Done:**
- [ ] 11+ Unit Tests grün
- [ ] Alle Constructor-Varianten abgedeckt
- [ ] Alle Enum-Mappings abgedeckt
- [ ] Edge Cases (leere Attribute, null-Werte) abgedeckt
- [ ] Test-DLLs als eingebettete Ressourcen im Test-Projekt

### 3.2 Unit Tests — StepRegistrar

- [ ] Mock für `IOrganizationService` erstellen (Moq/NSubstitute)
- [ ] Tests schreiben:
  - [ ] `RegisterSteps_NewStep_CallsCreate()`
  - [ ] `RegisterSteps_ExistingUnchangedStep_SkipsUpdate()`
  - [ ] `RegisterSteps_ExistingChangedStep_CallsUpdate()`
  - [ ] `RegisterSteps_WithImage_CreatesImage()`
  - [ ] `RegisterSteps_ChangedImage_UpdatesImage()`
  - [ ] `RegisterSteps_IsolationModeSet_CorrectOptionSetValue()`
  - [ ] `RegisterSteps_SecureConfig_CreatesSecureConfigEntity()`
  - [ ] `StepHasChanges_AllFieldsSame_ReturnsFalse()`
  - [ ] `StepHasChanges_StageChanged_ReturnsTrue()`
  - [ ] `StepHasChanges_FilteringAttributesChanged_ReturnsTrue()`
  - [ ] `FindOrphanedSteps_StepRemovedFromCode_ReturnsOrphan()`
  - [ ] `DeleteOrphanedSteps_CallsDeleteWithCorrectId()`

**Definition of Done:**
- [ ] 12+ Unit Tests grün
- [ ] Create/Update/Skip-Pfade alle getestet
- [ ] Change Detection für jedes Feld einzeln getestet
- [ ] Orphan Detection getestet
- [ ] Kein Test braucht eine echte Dataverse-Verbindung

### 3.3 Unit Tests — PackageDeployer

- [ ] Mock für `IOrganizationService` und `IFileSystem` erstellen
- [ ] Tests schreiben:
  - [ ] `Push_NewPackage_CallsCreate()`
  - [ ] `Push_ExistingPackage_CallsUpdate()`
  - [ ] `Push_SameVersion_SkipsUpload()`
  - [ ] `Push_WithSolution_AddsSolutionComponent()`
  - [ ] `Push_InvalidNupkg_ThrowsMeaningfulError()`
  - [ ] `Push_MissingNuspec_ThrowsMeaningfulError()`
  - [ ] `Push_FileNotFound_ThrowsFileNotFoundException()`
  - [ ] `ExtractMetadata_ValidNupkg_ReturnsIdAndVersion()`

**Definition of Done:**
- [ ] 8+ Unit Tests grün
- [ ] File I/O vollständig gemockt (kein Dateisystem-Zugriff in Tests)
- [ ] NuSpec-Parsing getestet
- [ ] Fehlerfälle mit aussagekräftigen Exception-Messages getestet

### 3.4 Unit Tests — Config & EnvFile

- [ ] Tests schreiben:
  - [ ] `Load_ValidConfig_DeserializesCorrectly()`
  - [ ] `Load_MissingFile_ReturnsError()`
  - [ ] `Load_InvalidJson_ReturnsError()`
  - [ ] `Resolve_WithPlaceholder_ReplacesFromEnv()`
  - [ ] `Resolve_WithMissingVar_FallsBackToSystemEnv()`
  - [ ] `Resolve_WithNoPlaceholders_ReturnsUnchanged()`
  - [ ] `EnvFile_CommentsIgnored()`
  - [ ] `EnvFile_QuotedValues_StripsQuotes()`
  - [ ] `EnvFile_BlankLines_Skipped()`

**Definition of Done:**
- [ ] 9+ Unit Tests grün
- [ ] Alle Placeholder-Varianten abgedeckt
- [ ] Edge Cases (leere Werte, fehlende Datei, ungültiges JSON) abgedeckt

### 3.5 Integration Tests (gegen echte Dataverse-Umgebung)

Diese Tests brauchen eine echte Dataverse Dev/Test-Umgebung.
Sie laufen **nicht** in CI/CD, sondern werden manuell oder per Nightly-Build ausgeführt.

- [ ] Test-Kategorie `[Trait("Category", "Integration")]` verwenden
- [ ] Eigene `appsettings.test.json` für Verbindungsdaten (nicht eingecheckt!)
- [ ] Tests:
  - [ ] `FullWorkflow_DeployAndRegister_StepsExistInDataverse()`
  - [ ] `FullWorkflow_UpdatePlugin_ChangesDetectedAndApplied()`
  - [ ] `FullWorkflow_RemoveStep_OrphanDetected()`
  - [ ] `Auth_InteractiveLogin_ReturnsValidService()`
  - [ ] `Auth_InvalidCredentials_ThrowsMeaningfulError()`
  - [ ] `Package_UploadAndVerify_ExistsInDataverse()`
- [ ] Cleanup: Tests räumen ihre erstellten Records nach jedem Lauf auf

**Definition of Done:**
- [ ] 6+ Integration Tests vorhanden
- [ ] Tests können mit `dotnet test --filter Category=Integration` ausgeführt werden
- [ ] Cleanup funktioniert zuverlässig (kein Datenmüll in der Test-Umgebung)
- [ ] `appsettings.test.json` ist in `.gitignore`

---

## Phase 4 — Manuelle Tests & Verifikation

> Diese Phase enthalt Tests, die du **selbst manuell** ausfuhren musst,
> um die Kernfunktionen in einer echten Umgebung zu verifizieren.
> Dokumentiere die Ergebnisse jeweils mit Screenshot oder Konsolenausgabe.

### 4.0 Agent-Reviews fur Phase 4

> Diese Agents laufen **nach** den manuellen Tests, als finale Qualitätsprufung.

- [ ] **Reality Checker** Agent: Produktionsreife bewerten
  - Default-Urteil ist "NEEDS WORK" — braucht uberzeugende Beweise fur "READY"
  - Pruft: Alle DoD-Checkboxen der Phasen 0-3 sind abgehakt
  - Pruft: Keine offenen Known Issues die ein Release blockieren
  - Pruft: Error Handling ist robust genug fur Produktionseinsatz
- [ ] **Evidence Collector** Agent: Systematisch nach ubersehenen Problemen suchen
  - Findet 3-5 Issues die wir ubersehen haben (das ist sein Default!)
  - Will Screenshots/Logs als Beweis fur jede Behauptung
  - Pruft: Edge Cases die in keinem Test vorkommen
- [ ] **Security Engineer** Agent: Finale Sicherheitsprufung
  - Pruft: Credentials werden nie geloggt (auch nicht mit `--verbose`)
  - Pruft: .env Dateien in .gitignore
  - Pruft: MSAL Token-Handling korrekt (Expiry, Refresh, Disposal)
  - Pruft: Keine Injection-Moglichkeiten uber CLI-Argumente

### 4.1 End-to-End: Frische Umgebung

Testet den kompletten Workflow in einer leeren Dataverse-Umgebung.

- [ ] Neue Dataverse Dev-Umgebung erstellen (oder bestehende leeren)
- [ ] `plugin-reg init` ausführen → `pluginreg.json` wird erstellt
- [ ] `pluginreg.json` mit korrekten Werten befüllen
- [ ] `.env` Datei mit Credentials anlegen
- [ ] `plugin-reg register` ausführen
- [ ] **Verifizieren in Dataverse:**
  - [ ] PluginPackage existiert mit korrekter Version
  - [ ] Alle Plugin Steps sind registriert
  - [ ] Step-Attribute stimmen (Stage, Mode, FilteringAttributes, Entity, Message)
  - [ ] Images sind korrekt registriert (Type, Name, Attributes)
  - [ ] SecureConfiguration ist korrekt gesetzt (falls vorhanden)
  - [ ] IsolationMode ist korrekt gesetzt
  - [ ] Solution Assignment stimmt
- [ ] `plugin-reg list` ausführen → Output zeigt alle Steps korrekt an

**Definition of Done:**
- [ ] Kompletter Workflow von init bis register funktioniert fehlerfrei
- [ ] Alle registrierten Entitäten in Dataverse manuell verifiziert
- [ ] Screenshots/Logs der Ergebnisse archiviert

### 4.2 End-to-End: Update-Szenario

Testet, ob Änderungen korrekt erkannt und angewendet werden.

- [ ] Ein bestehendes Plugin nehmen (aus 4.1)
- [ ] **Änderung 1:** FilteringAttributes eines Steps ändern
- [ ] **Änderung 2:** Einen neuen Step hinzufügen
- [ ] **Änderung 3:** Einen bestehenden Step entfernen
- [ ] **Änderung 4:** Package-Version hochzählen im `.nupkg`
- [ ] `plugin-reg register` ausführen
- [ ] **Verifizieren:**
  - [ ] Geänderter Step: FilteringAttributes aktualisiert
  - [ ] Neuer Step: korrekt erstellt
  - [ ] Entfernter Step: Orphan-Prompt erscheint, nach Bestätigung gelöscht
  - [ ] Unveränderte Steps: als "UNCHANGED" geloggt (kein unnötiger Update)
  - [ ] Package: neue Version hochgeladen

**Definition of Done:**
- [ ] Alle 4 Änderungstypen korrekt verarbeitet
- [ ] Change Detection unterscheidet zuverlässig zwischen geändert/unverändert
- [ ] Orphan Cleanup funktioniert nach Bestätigung
- [ ] Logs zeigen CREATE / UPDATE / UNCHANGED / DELETE korrekt an

### 4.3 End-to-End: Dry-Run Verifikation

- [ ] Plugin mit bekannten Änderungen vorbereiten
- [ ] `plugin-reg register --dry-run` ausführen
- [ ] **Verifizieren:**
  - [ ] Output zeigt alle geplanten Änderungen
  - [ ] WOULD CREATE / WOULD UPDATE / WOULD DELETE korrekt zugeordnet
  - [ ] **Danach in Dataverse prüfen: NICHTS hat sich geändert**
- [ ] Ohne `--dry-run` erneut ausführen
- [ ] **Verifizieren:** Änderungen jetzt tatsächlich angewendet

**Definition of Done:**
- [ ] Dry-Run zeigt exakt die gleichen Aktionen, die der echte Run dann ausführt
- [ ] Kein einziger Seiteneffekt bei `--dry-run`
- [ ] Manuell in Dataverse verifiziert

### 4.4 Fehlerszenarien manuell testen

- [ ] **Falsche Credentials:** Aussagekräftige Fehlermeldung? Kein Stacktrace?
- [ ] **Ungültige DLL-Pfad:** Klare Fehlermeldung mit Dateipfad?
- [ ] **DLL ohne CrmPluginRegistrationAttribute:** Leere Liste, kein Crash?
- [ ] **Ungültige pluginreg.json:** Parsing-Fehler mit Zeilennummer?
- [ ] **Netzwerk-Timeout:** Retry-Versuche sichtbar im Log?
- [ ] **Nicht vorhandene Umgebung:** Aussagekräftiger Fehler?
- [ ] **Fehlende Berechtigung:** Dataverse-Fehlermeldung wird durchgereicht?
- [ ] **Ctrl+C während Register:** Graceful Shutdown, keine halben Operationen?

**Definition of Done:**
- [ ] Jedes Fehlerszenario getestet und dokumentiert
- [ ] Keine Fehlermeldung zeigt einen rohen Stacktrace (nur mit `--verbose`)
- [ ] Jede Fehlermeldung enthält einen Hinweis, was der User tun kann
- [ ] Ctrl+C führt zu sauberem Abbruch

### 4.5 Multi-Assembly & Multi-Environment Test

- [ ] `pluginreg.json` mit 2+ Assemblies konfigurieren
- [ ] `pluginreg.json` mit 2+ Environments konfigurieren (dev, test)
- [ ] `plugin-reg register --env dev` → deployt nur in Dev
- [ ] `plugin-reg register --env test` → deployt nur in Test
- [ ] **Verifizieren:** Beide Umgebungen haben die korrekten Steps
- [ ] **Verifizieren:** Keine Cross-Contamination zwischen Umgebungen

**Definition of Done:**
- [ ] Multi-Assembly Deployment funktioniert
- [ ] Environment-Switching funktioniert
- [ ] Jede Umgebung hat nur ihre eigenen Steps

---

## Phase 5 — CLI Polish & UX

### 5.0 Agent-Reviews fur Phase 5

- [ ] **Technical Writer** Agent: Alle user-facing Texte reviewen
  - Pruft: CLI Help-Texte sind klar, konsistent und vollstandig
  - Pruft: Fehlermeldungen sind verstandlich und actionable
  - Pruft: Error-Code-Dokumentation in README ist aktuell
  - Pruft: Englische Texte sind grammatisch korrekt (kein "Denglisch")
- [ ] **Code Reviewer** Agent: Fehler-Code-System und Progress-Anzeige reviewen
  - Pruft: Fehler-Codes sind konsistent benannt
  - Pruft: `--json`, `--no-color`, `--verbose` Flags interagieren korrekt

### 5.1 Internationalisierung

Aktuell sind die Auth-Success-Page und einige Meldungen auf Deutsch.
Für internationale Adoption muss Englisch die Standardsprache sein.

- [ ] Alle Console-Ausgaben auf Englisch umstellen
- [ ] Auth Success/Error HTML auf Englisch umstellen
- [ ] Optional: `--language de` Flag für deutsche Ausgabe (nice-to-have, niedrige Prio)
- [ ] README.md auf Englisch verfassen (zusätzlich zur deutschen Version)

**Definition of Done:**
- [ ] Standardsprache ist Englisch
- [ ] Keine deutschen Strings mehr im Default-Output
- [ ] README.md existiert auf Englisch

### 5.2 Strukturierte Fehler-Ausgabe

- [ ] Fehler-Codes einführen:
  - `PRE001` — Config-Datei nicht gefunden
  - `PRE002` — Config-Datei ungültig
  - `PRE003` — DLL nicht gefunden
  - `PRE004` — DLL enthält keine Plugin-Attribute
  - `AUTH001` — Authentifizierung fehlgeschlagen
  - `AUTH002` — Token abgelaufen, Retry fehlgeschlagen
  - `REG001` — Step-Registrierung fehlgeschlagen
  - `REG002` — Image-Registrierung fehlgeschlagen
  - `PKG001` — Package-Upload fehlgeschlagen
  - `PKG002` — NuSpec nicht lesbar
  - `NET001` — Netzwerk-Timeout
  - `NET002` — Service nicht erreichbar
- [ ] Fehler-Format: `ERROR [PRE003]: DLL not found: path/to/plugin.dll`
- [ ] Bei `--verbose`: Zusätzlich Stacktrace und Inner Exception
- [ ] `--json` Flag: Fehler als JSON für Scripting:
  ```json
  {"error": "PRE003", "message": "DLL not found", "path": "path/to/plugin.dll"}
  ```

**Definition of Done:**
- [ ] Jeder Fehler hat einen eindeutigen Code
- [ ] Fehler-Codes sind in README/Hilfe dokumentiert
- [ ] `--json` gibt maschinenlesbaren Output

### 5.3 Progress-Anzeige verbessern

- [ ] Fortschrittsanzeige für Step-Registrierung: `[3/12] Registering PostOperation of Update on contact...`
- [ ] Zusammenfassung am Ende:
  ```
  Registration complete:
    Package:  MyPlugin v1.2.0 (uploaded)
    Steps:    8 created, 2 updated, 2 unchanged, 1 deleted
    Images:   3 created, 1 updated
    Duration: 12.4s
  ```
- [ ] Farbige Ausgabe (optional, mit `--no-color` abschaltbar):
  - Grün: Created, Success
  - Gelb: Updated, Warning
  - Grau: Unchanged, Skipped
  - Rot: Error, Deleted

**Definition of Done:**
- [ ] Fortschritt ist für jeden Step sichtbar
- [ ] Zusammenfassung zeigt alle Aktionen aggregiert
- [ ] Dauer wird gemessen und angezeigt
- [ ] `--no-color` funktioniert (für CI/CD Logs)

---

## Phase 6 — Branding, Copyright & Sichtbarkeit

Das Tool soll klar mit deinem Namen assoziiert werden. Subtil aber konsistent —
an den richtigen Stellen Copyright-Hinweise und den BuyMeACoffee-Link platzieren,
ohne aufdringlich zu wirken.

### 6.0 Agent-Reviews fur Phase 6

- [ ] **Brand Guardian** Agent: Branding-Konsistenz prufen
  - Pruft: Copyright-Text ist uberall identisch formuliert
  - Pruft: BuyMeACoffee-Link ist korrekt und konsistent
  - Pruft: Ton und Platzierung sind professionell, nicht aufdringlich
  - Pruft: NuGet, GitHub, README, CLI Output, Auth Page — alles konsistent
- [ ] **Technical Writer** Agent: README und NuGet Description finalisieren
  - Pruft: Englische Texte sind fehlerfrei
  - Pruft: README hat klare Struktur: Install → Quickstart → Config → Reference

### 6.1 Copyright & Lizenz-Header

- [ ] Copyright-Header in jede `.cs` Datei einfügen:
  ```csharp
  // Copyright (c) Robert Stickler. All rights reserved.
  // Licensed under the MIT License. See LICENSE file in the project root.
  ```
- [ ] `.editorconfig` Regel erstellen, die den Header bei neuen Dateien automatisch einfügt
- [ ] `LICENSE` Datei erstellen (MIT) mit vollem Namen:
  ```
  MIT License

  Copyright (c) 2025-present Robert Stickler

  Permission is hereby granted, free of charge, ...
  ```
- [ ] `AssemblyInfo` Attribute in `.csproj` setzen:
  ```xml
  <Copyright>Copyright (c) Robert Stickler</Copyright>
  <Authors>Robert Stickler</Authors>
  <Company>rstickler.dev</Company>
  ```

**Definition of Done:**
- [ ] Jede `.cs` Datei hat den Copyright-Header
- [ ] LICENSE existiert mit vollem Namen
- [ ] NuGet Package Metadata zeigt den korrekten Autor
- [ ] `dotnet pack` enthält Copyright-Info im `.nupkg`

### 6.2 BuyMeACoffee Integration — Console Output

Der Link soll an strategischen Stellen erscheinen, wo der User ihn sieht
und positiv gestimmt ist (nach Erfolg), aber **nie** bei Fehlern oder mitten
im Workflow.

- [ ] **Nach erfolgreichem `register`** (bereits vorhanden — beibehalten):
  ```
  Registration complete!
    Package:  MyPlugin v1.2.0 (uploaded)
    Steps:    8 created, 2 updated, 2 unchanged

  Made with love by rstickler.dev
  Like this tool? https://buymeacoffee.com/rstickler.dev
  ```
- [ ] **Bei `--help` / Hilfetext** — ganz am Ende:
  ```
  plugin-reg v1.0.0 — Dataverse Plugin Registration Tool
  Copyright (c) Robert Stickler | https://buymeacoffee.com/rstickler.dev

  Commands:
    init        Create pluginreg.json config file
    register    Deploy package and register steps
    list        List discovered steps (no connection needed)
  ...
  ```
- [ ] **Bei `--version`**:
  ```
  plugin-reg v1.0.0
  Copyright (c) Robert Stickler
  https://buymeacoffee.com/rstickler.dev
  ```
- [ ] **Bei `init`** — nach erfolgreicher Erstellung:
  ```
  Created pluginreg.json — edit it with your environment details.

  Need help? https://github.com/<repo>
  Like this tool? https://buymeacoffee.com/rstickler.dev
  ```
- [ ] **NICHT bei:**
  - [ ] Fehlermeldungen (wirkt unangemessen)
  - [ ] `--json` Output (maschinenlesbar, kein Marketing)
  - [ ] `--quiet` Modus (falls implementiert)
  - [ ] Mitten in der Step-Registrierung (stört den Workflow)

**Definition of Done:**
- [ ] BuyMeACoffee-Link erscheint bei: register success, init success, --help, --version
- [ ] Link erscheint NIE bei Fehlern oder maschinenlesbarem Output
- [ ] Konsistentes Format: immer als letzte Zeile(n) im Block

### 6.3 BuyMeACoffee Integration — Auth Success Page

Die MSAL Browser-Seite nach erfolgreichem Login ist ein idealer Touchpoint —
der User sieht sie genau einmal pro Session und ist gerade positiv gestimmt.

- [ ] Auth Success Page auf Englisch umstellen (Internationalisierung, Phase 5.1)
- [ ] Design aufwerten:
  ```html
  <div class="success">
    <h1>Connected!</h1>
    <p>Plugin deployment is running in your terminal.</p>
    <p>You can close this tab.</p>
    <hr/>
    <p class="branding">
      <strong>Dataverse.PluginRegistration</strong> by Robert Stickler
    </p>
    <p class="coffee">
      Like this tool?
      <a href="https://buymeacoffee.com/rstickler.dev">Buy me a coffee</a>
    </p>
  </div>
  ```
- [ ] Logo/Icon einbinden (falls vorhanden, als inline SVG oder Base64)
- [ ] BuyMeACoffee-Button statt nur Link (optisch ansprechender):
  ```html
  <a href="https://buymeacoffee.com/rstickler.dev" class="bmc-button">
    Buy me a coffee
  </a>
  ```

**Definition of Done:**
- [ ] Auth Success Page ist auf Englisch
- [ ] Seite zeigt Autor-Name und BuyMeACoffee-Link prominent
- [ ] Design ist professionell und nicht überladen
- [ ] Link öffnet korrekt in neuem Tab

### 6.4 Branding in NuGet Package & GitHub

- [ ] **NuGet Package Description** — mit Author-Branding:
  ```
  Dataverse plugin registration CLI for NuGet-based plugins.
  Reads CrmPluginRegistrationAttribute and registers steps + images —
  like spkl, but for PluginPackages.

  By Robert Stickler (rstickler.dev)
  ```
- [ ] **GitHub Repository:**
  - [ ] "About" Beschreibung setzen
  - [ ] Topics/Tags: `dynamics365`, `dataverse`, `plugin`, `nuget`, `cli`, `dotnet`
  - [ ] Social Preview Image erstellen (1280x640 px) mit Tool-Name und Tagline
- [ ] **README.md Branding:**
  - [ ] Header-Badge: NuGet Version, Downloads, License
  - [ ] Footer:
    ```markdown
    ---
    Made with love by [Robert Stickler](https://buymeacoffee.com/rstickler.dev)

    If this tool saves you time, consider [buying me a coffee](https://buymeacoffee.com/rstickler.dev).
    ```

**Definition of Done:**
- [ ] NuGet, GitHub und README zeigen konsistentes Branding
- [ ] BuyMeACoffee-Link ist auf GitHub README sichtbar
- [ ] Social Preview Image existiert und ist auf GitHub gesetzt

---

## Phase 7 — CI/CD & Veröffentlichung

### 7.0 Agent-Reviews fur Phase 7

- [ ] **DevOps Automator** Agent: GitHub Actions Workflows reviewen und optimieren
  - Pruft: Build-Matrix ist sinnvoll (OS-Abdeckung)
  - Pruft: Secrets sind korrekt referenziert, keine Hardcoded Credentials
  - Pruft: Release-Workflow ist idempotent (doppelter Tag-Push crasht nicht)
- [ ] **Git Workflow Master** Agent: Branching-Strategie und Release-Prozess reviewen
  - Pruft: Tag-Konvention (v1.0.0) ist konsistent
  - Pruft: Branch Protection Rules sind konfiguriert
  - Pruft: Conventional Commits werden eingehalten
- [ ] **Security Engineer** Agent: CI/CD Security reviewen
  - Pruft: NuGet API Key Scope ist minimal (nur Push, nur dieses Package)
  - Pruft: Keine Secrets in Build-Logs
  - Pruft: Dependency-Scanning ist integriert
- [ ] **Reality Checker** Agent: Finale Release-Freigabe
  - Letzter Gate-Check vor v1.0.0 Veroffentlichung
  - Alle Phasen 0-6 mussen "DONE" sein
  - Alle vorherigen Agent-Reviews mussen bestanden sein

### 7.1 GitHub Actions Workflow

- [ ] `.github/workflows/build.yml` erstellen:
  - Trigger: Push auf `main`, Pull Requests
  - Steps:
    - [ ] Checkout
    - [ ] .NET 10 SDK Setup
    - [ ] `dotnet restore`
    - [ ] `dotnet build --configuration Release`
    - [ ] `dotnet test --filter Category=Unit` (nur Unit Tests in CI)
    - [ ] `dotnet pack --configuration Release`
  - Matrix: `windows-latest`, `ubuntu-latest`, `macos-latest`
- [ ] `.github/workflows/release.yml` erstellen:
  - Trigger: Tag `v*` (z.B. `v1.0.0`)
  - Steps:
    - [ ] Build + Test (wie oben)
    - [ ] `dotnet nuget push` zu NuGet.org
    - [ ] GitHub Release erstellen mit Changelog
  - Secrets: `NUGET_API_KEY` in GitHub Repository Settings

**Definition of Done:**
- [ ] Jeder Push auf main löst Build + Tests aus
- [ ] PRs können nicht gemergt werden wenn Tests fehlschlagen (Branch Protection)
- [ ] Tag-basiertes Release publiziert automatisch auf NuGet.org
- [ ] Build läuft auf Windows, Linux und macOS

### 7.2 NuGet.org Vorbereitung

- [ ] NuGet.org Account erstellen (falls noch nicht vorhanden)
- [ ] API Key generieren (Scope: Push new packages)
- [ ] `.csproj` Metadata vervollständigen:
  - [ ] `<PackageId>Dataverse.PluginRegistration</PackageId>` (prüfen ob Name frei ist!)
  - [ ] `<Authors>` — Eure Namen
  - [ ] `<Description>` — Englische Beschreibung, Keywords für Discoverability
  - [ ] `<PackageTags>dynamics365 dataverse plugin registration nuget cli</PackageTags>`
  - [ ] `<PackageLicenseExpression>MIT</PackageLicenseExpression>`
  - [ ] `<PackageProjectUrl>` — GitHub Repository URL
  - [ ] `<RepositoryUrl>` — GitHub Repository URL
  - [ ] `<PackageIcon>` — Icon erstellen (128x128 PNG)
  - [ ] `<PackageReadmeFile>README.md</PackageReadmeFile>`
- [ ] `LICENSE` Datei erstellen (MIT empfohlen für maximale Adoption)
- [ ] Prüfen: Ist der Package-Name `Dataverse.PluginRegistration` auf NuGet.org noch frei?
  - Falls nicht: Alternative wählen (z.B. `Dataverse.PluginRegistration.CLI`)

**Definition of Done:**
- [ ] Package-Name auf NuGet.org reserviert
- [ ] Alle NuGet Metadata ausgefüllt
- [ ] LICENSE Datei existiert
- [ ] `dotnet pack` erstellt ein valides `.nupkg` mit korrektem Metadata

### 7.3 Erste Veröffentlichung (v1.0.0)

- [ ] Changelog erstellen (`CHANGELOG.md`) mit allen Features der v1.0.0
- [ ] Version in `.csproj` auf `1.0.0` setzen
- [ ] Finaler manueller Test (Phase 4 komplett durchlaufen)
- [ ] Git Tag erstellen: `git tag v1.0.0`
- [ ] Tag pushen: `git push origin v1.0.0`
- [ ] GitHub Actions Release Workflow verifizieren
- [ ] Auf NuGet.org verifizieren:
  - [ ] Package ist sichtbar
  - [ ] `dotnet tool install -g Dataverse.PluginRegistration` funktioniert
  - [ ] `plugin-reg --help` zeigt Hilfe an
  - [ ] `plugin-reg register --dry-run` funktioniert mit echtem Plugin
- [ ] README auf GitHub aktualisieren mit NuGet Badge und Install-Befehl
- [ ] Announcement vorbereiten (LinkedIn, Dynamics Community, Reddit r/Dynamics365)

**Definition of Done:**
- [ ] Package auf NuGet.org live und installierbar
- [ ] Installation via `dotnet tool install -g Dataverse.PluginRegistration` verifiziert
- [ ] GitHub Release mit Changelog existiert
- [ ] README enthält NuGet Badge und Install-Anweisung
- [ ] Mindestens 1 externer Tester hat das Tool erfolgreich installiert und benutzt

### 7.4 Post-Launch: Community-Adoption

- [ ] GitHub Issues aktivieren fur Bug Reports und Feature Requests
- [ ] Contributing Guide erstellen (`CONTRIBUTING.md`)
- [ ] In Dynamics 365 Community bekannt machen:
  - [ ] LinkedIn Post mit Demo-Video
  - [ ] Reddit r/Dynamics365, r/dotnet
  - [ ] Power Platform Community Forum
  - [ ] XrmToolBox Community (bekannte Zielgruppe)
- [ ] Demo/Prasentation vorbereiten fur Konferenzen oder Meetups
- [ ] Feedback sammeln und v1.1.0 Roadmap erstellen

**Definition of Done:**
- [ ] Aktive Feedback-Kanale eingerichtet
- [ ] Erste externe Nutzer ausserhalb des eigenen Teams
- [ ] Mindestens 2 Community-Posts veroffentlicht

---

## Zusammenfassung der Phasen

| Phase | Fokus | Items | Komplexitat |
|-------|-------|-------|-------------|
| **Phase 0** | Architektur + .NET 10 + Sofort-Fixes | 0.0-0.5 | Hoch |
| **Phase 1** | Bugs fixen (7 kritische) | 1.0-1.7 | Hoch — mehr Bugs als erwartet |
| **Phase 2** | Kern-Features + Auth + Retry | 2.0-2.7 | Hoch |
| **Phase 3** | Test-Suite | 3.0-3.5 | Mittel |
| **Phase 4** | Manuelle Tests + Agent Gate-Check | 4.0-4.5 | Mittel |
| **Phase 5** | CLI Polish & UX | 5.0-5.3 | Niedrig-Mittel |
| **Phase 6** | Branding & Copyright | 6.0-6.4 | Niedrig |
| **Phase 7** | CI/CD & Veroffentlichung | 7.0-7.4 | Mittel |
| **Backlog** | Post-v1.0 Features | 18 Items | Nach Community-Feedback |

> **Empfohlene Reihenfolge:** 0 → 1 → 3.1 → 2 → 3.2-3.5 → 4 → 5 → 6 → 7
>
> Phase 0 zuerst, weil ohne testbare Architektur keine sinnvollen Tests geschrieben
> werden konnen. Phase 3.1 (AttributeReader Tests) direkt nach den Bugfixes, weil
> der AttributeReader keine Architektur-Anderungen braucht und ihr damit sofort
> Erfahrung mit dem Test-Setup sammelt. Phase 6 (Branding) vor der Veroffentlichung,
> damit Copyright und BuyMeACoffee von Tag 1 im veroffentlichten Package sind.

---

## Agent-Workflow Ubersicht

So setzen wir die Agents ein — nach jedem Arbeitspaket die passenden Reviews:

```
Phase 0 (Architektur)
  └─ Code schreiben → Software Architect + Code Reviewer + Security Engineer + /simplify

Phase 1 (Bugfixes)
  └─ Fix implementieren → Code Reviewer + Security Engineer (fur SecureConfig) + /simplify

Phase 2 (Features)
  └─ Feature bauen → Backend Architect (Retry) + Code Reviewer + Security Engineer (Deletion) + /simplify

Phase 3 (Tests)
  └─ Tests schreiben → Test Results Analyzer + Code Reviewer

Phase 4 (Manuelle Tests)
  └─ Du testest manuell → Reality Checker + Evidence Collector + Security Engineer

Phase 5 (CLI Polish)
  └─ UX verbessern → Technical Writer + Code Reviewer

Phase 6 (Branding)
  └─ Branding einbauen → Brand Guardian + Technical Writer

Phase 7 (Release)
  └─ CI/CD aufsetzen → DevOps Automator + Git Workflow Master + Security Engineer
  └─ Vor Release → Reality Checker (finale Freigabe)
```

**Grundregel:** Kein Arbeitspaket gilt als "done" bis der zugehorige Agent-Review bestanden ist.
Die `.0` Checkboxen jeder Phase sind der Gate-Check bevor wir zur nachsten Phase weitergehen.

---

## Positionierung: Warum dieses Tool und nicht PAC CLI?

> Jeder potenzielle Nutzer wird fragen: "Warum nicht einfach `pac plugin push`?"

### Was PAC CLI kann
- `pac plugin push` — ladt ein NuGet Package nach Dataverse hoch
- Keine Step-Registrierung, keine Change Detection, kein Dry-Run
- Kein attribut-basierter Workflow (Steps mussen manuell oder per Plugin Registration Tool registriert werden)

### Was wir besser machen
| Feature | PAC CLI | plugin-reg |
|---------|---------|------------|
| Package Upload | Ja | Ja |
| Step-Registrierung aus Code-Attributen | Nein | Ja |
| Change Detection (nur andern was sich geandert hat) | Nein | Ja |
| Dry-Run (sehen was passiert bevor es passiert) | Nein | Ja |
| Orphan Cleanup (verwaiste Steps loschen) | Nein | Ja |
| Image-Registrierung | Nein | Ja |
| Multi-Environment Config | Begrenzt | Ja |
| CI/CD headless Auth | Ja | Ja (ab Phase 2.2) |
| Solution-aware Registration | Nein | Ja (ab Phase 1.4) |

### Kernaussage
> "PAC CLI deployt Packages. Wir deployen Packages **und** registrieren Steps —
> attribut-basiert, mit Change Detection, direkt aus dem Code. Wie spkl, aber fur
> die neue Plugin-Package-Welt."

### Risiko: Microsoft baut Step-Registrierung in PAC CLI
Falls Microsoft `pac plugin register-steps` einfuhrt, bleiben unsere Differenzierungsmerkmale:
- **Attribut-basierte Konfiguration** — Steps sind im Code definiert, nicht in einer Manifest-Datei
- **Change Detection** — nur andern was sich wirklich geandert hat
- **Dry-Run** — Vorschau vor Deployment
- **Orphan Cleanup** — automatisches Aufraumen verwaister Steps

### Feature-Vergleich mit spkl (Ehrlichkeit)
| Feature | spkl | plugin-reg |
|---------|------|------------|
| Plugin Step Registration | Ja | Ja |
| Workflow Activity Registration | Ja | Nein (bewusst, da deprecated) |
| WebResource Deployment | Ja | Nein (out of scope) |
| Early-Bound Generation | Ja | Nein (out of scope, `pac modelbuilder` existiert) |
| Solution Packaging | Ja | Nein (out of scope, `pac solution` existiert) |
| NuGet PluginPackage Support | Nein | Ja |
| Change Detection | Nein | Ja |
| Dry-Run | Nein | Ja |

**Fazit:** Wir ersetzen nicht spkl komplett — wir ersetzen den Plugin-Deployment-Teil
und machen ihn besser, fur die Zukunft (PluginPackages).

---

## Backlog: Post-v1.0 Features

> Features die sinnvoll sind, aber nicht fur v1.0.0 blockieren.
> Priorisierung basiert auf Community-Feedback nach Launch.

### DX-Verbesserungen (Developer Experience)

- [ ] **`plugin-reg validate` Befehl**
  - Validiert `pluginreg.json` Syntax
  - Pruft ob DLL-Pfade existieren
  - Pruft ob DLL Plugin-Attribute enthalt
  - Optional: Dataverse-Verbindung testen
  - Nicht-destruktiv, ideal fur CI/CD Pre-Checks

- [ ] **`plugin-reg status` Befehl**
  - Zeigt aktuellen Zustand: was ist in Dataverse, was ist im Code, was ist der Diff?
  - Wie `git status` vs `git commit --dry-run`
  - Braucht Dataverse-Verbindung aber andert nichts

- [ ] **`plugin-reg unregister` Befehl**
  - Entfernt ein Plugin komplett aus Dataverse (Package + alle Steps + alle Images)
  - Fur Environment-Cleanup, Teardown, oder fehlgeschlagene Experimente
  - Mit `--confirm` Sicherheitsabfrage

- [ ] **`init` erstellt `.env.example`**
  - Neben `pluginreg.json` auch eine `.env.example` Template-Datei erzeugen
  - Zeigt welche Variablen gesetzt werden mussen

- [ ] **spkl-Migrations-Guide / `plugin-reg migrate --from-spkl`**
  - Liest `spkl.json` und generiert `pluginreg.json`
  - Senkt die Adoptions-Hurde fur existierende spkl-User enorm

- [ ] **`dotnet new` Template fur Plugin-Projekte**
  - `dotnet new plugin-dataverse` scaffolded ein Projekt mit:
    - Korrektem `.csproj` fur PluginPackage
    - `pluginreg.json` Template
    - `.env.example`
    - Beispiel-Plugin mit `CrmPluginRegistrationAttribute`

- [ ] **IDE Integration Artefakte**
  - VS Code `tasks.json` fur `plugin-reg register`
  - VS Code `launch.json` fur Debugging
  - Visual Studio External Tool Definition

- [ ] **Shell Completion (Tab-Vervollstandigung)**
  - Bash, Zsh, Fish, PowerShell
  - Evaluieren ob `System.CommandLine` sich lohnt als CLI-Framework

- [ ] **`--quiet` Flag**
  - Unterdrukt alle Ausgaben ausser Fehlern
  - Wichtig fur CI/CD Scripts die nur Exit-Codes brauchen

- [ ] **Auto-Update Check**
  - Einmal pro 24h gegen NuGet API prufen ob neue Version existiert
  - `"A new version (1.2.0) is available. Run 'dotnet tool update -g Dataverse.PluginRegistration'"`
  - Abschaltbar mit `--no-update-check`

### Technische Verbesserungen

- [ ] **ExecuteMultiple Batching**
  - Statt einzelner Create/Update Calls: `ExecuteMultipleRequest` mit Batches von 20
  - Reduziert Round-Trips bei 50+ Steps dramatisch
  - Komplementar zu Retry-Logik (2.6)

- [ ] **Paginierung bei Dataverse-Queries**
  - `RetrieveMultiple` gibt max 5.000 Records zuruck
  - Bei grossen Organisationen konnten PluginType-Queries abgeschnitten werden
  - Paging Cookie implementieren

- [ ] **CancellationToken an Dataverse-Operationen durchreichen**
  - Aktuell stoppt Ctrl+C erst nach dem aktuellen Dataverse-Call
  - `ServiceClient` hat async Methoden mit CancellationToken-Support

- [ ] **Concurrency Guard (Idempotenz)**
  - Zwei Entwickler deployen gleichzeitig → Race Condition bei Step-Creation
  - "Duplicate Record" Fehler abfangen und in Update konvertieren

- [ ] **Streaming fur grosse nupkg Dateien**
  - Aktuell: `File.ReadAllBytes()` + Base64 = 3x Speicherverbrauch
  - Bei 50 MB Packages: 150 MB RAM Spike
  - Streaming oder Chunking implementieren

### Feature-Erweiterungen

- [ ] **Step Enable/Disable Befehl**
  - `plugin-reg disable --step "StepName"` / `plugin-reg enable --step "StepName"`
  - Fur temporares Deaktivieren wahrend Datenmigrationen
  - spkl kann das nicht — ware ein Alleinstellungsmerkmal

- [ ] **Workflow Activity Registrierung**
  - Parser existiert bereits (gibt aktuell `null` zuruck)
  - Registrierung uber `PluginType` mit `WorkflowActivityGroupName`
  - Niedrige Prioritat da Workflow Activities deprecated sind

- [ ] **Plugin Assembly (non-Package) Support**
  - Fur Teams die noch nicht auf NuGet migriert haben
  - Upload einer DLL als `pluginassembly` Entity
  - Backward-Compatibility Modus
