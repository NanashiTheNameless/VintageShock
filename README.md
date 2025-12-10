# VintageShock

Shocking Haptic Feedback for Vintage Story via the OpenShock API based on in-game events.

## !! **NON-FREE/PROPRIETARY License Warning**

This software is NOT OSI Compliant!\
See the [LICENSE.md](<LICENSE.md>) file for more complete details.

## NNCL v1.3 License TL;DR

**This is a simplified summary and does not replace the full license terms.**

### What You Can Do

- Use, study, modify, and share the work for free
- Create and distribute modified versions
- Deploy it for personal and non-commercial purposes

### What You Must Do

- Provide proper attribution to the licensor (NanashiTheNameless)
- Provide source code if you distribute compiled versions
- Include the full license with all distributions
- Maintain the ethical policy in your adaptations

### What You Cannot Do

- Use it for commercial purposes (sales, subscriptions, SaaS, ads, etc.)
- Train commercial AI or ML models with it
- Use it to enable discrimination or violence
- Alter or remove the license terms
- Deploy it for law enforcement, military, or immigration enforcement

### Key Points

- All modified versions must use the same license
- The licensor can revoke your rights with 7 days notice
- No warranties or guarantees are provided
- Large-scale deployments (100+ users) require public compliance documentation
- Filing patent lawsuits automatically terminates your license

### Bottom Line

Free for ethical, non-commercial use, with the requirement to share your code and keep the license intact.

## Features

- **OpenShock API Integration**: Trigger shocks through the OpenShock device API with full authentication
- **ConfigLib Integration**: Easy configuration through ConfigLib's in-game settings menu
- **Configurable Shock Triggers**:
  - Player death events
  - Player taking damage
  - Player damaging other players
- **Client Commands**: Test and manage your shock settings with `.vshock` commands

## Requirements

- [ConfigLib](<https://mods.vintagestory.at/configlib>) - For configuration management
- [ImGui](<https://mods.vintagestory.at/imgui>) - Required by ConfigLib

## Configuration

Configuration is managed through ConfigLib. Access settings via:

1. In-game: ConfigLib settings menu
2. Manual edit: `VintagestoryData/ModConfig/vintageshock.yaml`

### Configuration Options

- **Enabled**: Enable or disable the entire mod
- **API URL**: The full URL to your shock API endpoint (default: `https://api.openshock.app/`)
- **API Token**: Bearer token for authentication with the OpenShock API (get from https://app.openshock.app/)
- **Device ID**: The ID of the device you want to trigger
- **Intensity**: Shock intensity (0-100)
- **Duration**: Shock duration in milliseconds (300-65535)
- **OnPlayerDeath**: Trigger shock when you die
- **OnPlayerDamage**: Trigger shock when you take damage
- **OnPlayerHurtOther**: Trigger shock when you hurt others

## Client Commands

- `.vshock reload` - Reload configuration from ConfigLib
- `.vshock test` - Send a test shock to verify API connection
- `.vshock status` - Display current configuration
- `.vshock set` - Shows how to edit settings

## Building

Requirements:

- .NET SDK 8.0+
- Vintage Story 1.21.5 or newer

Build command:

```bash
dotnet build VintageShock.sln -c Release /p:GameDir="/path/to/VintageStory"
```

The mod will be packaged as `VintageShock-<version>.zip` in the workspace root.

## Installation

1. Download or build `VintageShock.zip`
2. Copy the zip file to your Vintage Story `Mods` folder
3. Install [ConfigLib](<https://mods.vintagestory.at/configlib>) and [ImGui](<https://mods.vintagestory.at/imguiif>) not already installed
4. Start the game and configure via ConfigLib settings menu or edit `ModConfig/vintageshock.yaml`

## Usage

1. Get your OpenShock API token from <https://app.openshock.app/>
2. Get your device ID from your OpenShock device
3. Configure via ConfigLib in-game menu or edit the YAML file directly (copypaste doesnt appear to work in ConfigLib so directly editing the .yaml may be preferrable)
4. Use `.vshock test` to verify your setup works
5. Play the game - shocks will trigger based on your configured events

## OpenShock API Details

API Endpoint: `https://api.openshock.app/`

Request format:

```json
{
  "device_id": "your-device-id",
  "duration_ms": 300,
  "intensity": 30
}
```

Authentication: AOI token in `Open-Shock-Token` header

## Notes

- Shocks are triggered asynchronously to prevent game lag
- Configuration is managed through ConfigLib
- ConfigLib's ImGui-based GUI doesn't support clipboard paste - edit the YAML file directly for easier token/device ID entry

## Support

For issues or suggestions, visit the GitHub repository.
