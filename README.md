# Dataverse Plugin Registration Tool

Dataverse plugin registration tool for **NuGet-based (dependent assembly) plugins**.
Reads `CrmPluginRegistrationAttribute` and registers steps + images — like spkl, but for PluginPackages.

## Installation

```bash
# From NuGet (when published)
dotnet tool install --global Dataverse.PluginRegistration

# Or from local build
dotnet pack
dotnet tool install --global --add-source ./nupkg Dataverse.PluginRegistration
```

## Quick Start

```bash
# In your plugin project directory:
plugin-reg init                  # Creates pluginreg.json + .env template
plugin-reg list                  # Dry-run: shows discovered steps
plugin-reg register --env dev    # Deploy package + register steps
```

## Commands

| Command    | Description |
|------------|-------------|
| `init`     | Create a `pluginreg.json` config file in the current directory |
| `register` | Push NuGet package + register plugin steps in Dataverse |
| `list`     | List discovered steps from assembly (dry-run, no connection needed) |

## Configuration

### pluginreg.json

```json
{
  "assemblies": [
    {
      "name": "MyPlugin",
      "path": "bin\\Debug\\net462\\MyPlugin.dll",
      "nupkgPath": "bin\\Debug\\MyPlugin.1.0.0.nupkg",
      "publisherPrefix": "pub",
      "solutionName": "MySolution_unmanaged"
    }
  ],
  "environments": {
    "dev": {
      "url": "${DATAVERSE_DEV_URL}",
      "authType": "OAuth",
      "appId": "${DATAVERSE_APPID}",
      "redirectUri": "${DATAVERSE_REDIRECT_URI}",
      "loginPrompt": "Auto"
    }
  }
}
```

### .env

```env
DATAVERSE_APPID=51f81489-12ee-4a9e-aaae-a2591f45987d
DATAVERSE_REDIRECT_URI=http://localhost
DATAVERSE_DEV_URL=https://myorg-dev.crm4.dynamics.com
DATAVERSE_LIVE_URL=https://myorg.crm4.dynamics.com
```

## What it does

1. **Step 1/2 — Push Plugin Package**: Creates or updates the PluginPackage in Dataverse from the `.nupkg` file
2. **Step 2/2 — Register Steps**: Reads `CrmPluginRegistrationAttribute` from the DLL and syncs steps + images with change detection (only updates when something changed)

## Features

- Reads `CrmPluginRegistrationAttribute` via `MetadataLoadContext` (no assembly locking)
- NuGet package deployment (replaces `pac plugin push`)
- Step + image registration with **change detection** (CREATED / UPDATED / UNCHANGED)
- `.env` file support with `${VAR}` placeholder resolution
- Custom browser authentication page (German)
- Ctrl+C cancellation support

## CLI Options

```
plugin-reg register --env <name>              Use named environment from pluginreg.json
plugin-reg register --dll <path>              Override DLL path
plugin-reg register --connection <string>     Use raw connection string
plugin-reg register --config <path>           Custom config file path
plugin-reg register --assembly-name <name>    Override assembly name
```
