# Jellyfin Plugin Adult Metadata

This plugin provides metadata for adult movies from GEVI and AEBN.

## Installation

1. Download the latest release from the releases page.
2. Extract the DLL and place it in the Jellyfin plugins directory.
3. Restart Jellyfin.

## Configuration

In Jellyfin dashboard, go to Plugins > Adult Metadata Provider to configure.

## Building

```bash
dotnet build
```

## Deployment on OMV with Docker

1. Clone this repo on your server.
2. Build the plugin: `dotnet publish -c Release -o publish`
3. Copy the DLL from publish to your Jellyfin plugins folder (usually /config/plugins in Docker).
4. Restart the Jellyfin container.

For Docker compose, ensure the plugins volume is mounted.