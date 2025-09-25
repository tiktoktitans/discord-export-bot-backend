# Discord Export Bot - Backend

## Overview
Discord bot that exports channel messages to various formats using slash commands.

## Requirements
- .NET 9 SDK
- Discord Bot Token

## Setup

### 1. Configure Bot Token
Set your Discord bot token in one of these ways:
```bash
export DISCORD_BOT_TOKEN="your-bot-token-here"
```
Or add to `appsettings.json`:
```json
{
  "Discord": {
    "BotToken": "your-bot-token-here"
  }
}
```

### 2. Build and Run
```bash
dotnet build
dotnet run
```

## Commands
- `/export` - Export channel messages
  - `limit` - Number of messages (default: 100)
  - `format` - Export format (html, txt, json, csv)
- `/export-help` - Show help information

## Deployment

### Railway
1. Connect GitHub repository
2. Set `DISCORD_BOT_TOKEN` environment variable
3. Deploy

### Docker
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /app
COPY *.csproj ./
RUN dotnet restore
COPY . ./
RUN dotnet publish -c Release -o out

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app/out .
ENTRYPOINT ["dotnet", "DiscordExportBot.dll"]
```

## Environment Variables
- `DISCORD_BOT_TOKEN` - Required Discord bot token

## Bot Permissions
Required Discord permissions:
- Send Messages
- Read Message History
- Attach Files
- Use Slash Commands