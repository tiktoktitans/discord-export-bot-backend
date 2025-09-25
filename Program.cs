using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordExportBot;

public class Program
{
    private static DiscordSocketClient? _client;
    private static IConfiguration? _configuration;
    private static string? _botToken;

    public static async Task Main(string[] args)
    {
        // Load configuration
        _configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();

        _botToken = Environment.GetEnvironmentVariable("DISCORD_BOT_TOKEN") ?? _configuration["Discord:BotToken"];

        if (string.IsNullOrEmpty(_botToken))
        {
            Console.WriteLine("❌ Bot token not found!");
            Console.WriteLine("Set DISCORD_BOT_TOKEN environment variable or add to appsettings.json");
            return;
        }

        // Create Discord client
        var config = new DiscordSocketConfig
        {
            LogLevel = LogSeverity.Info,
            MessageCacheSize = 100,
            GatewayIntents = GatewayIntents.Guilds |
                           GatewayIntents.GuildMessages |
                           GatewayIntents.MessageContent
        };

        _client = new DiscordSocketClient(config);

        // Subscribe to events
        _client.Log += LogAsync;
        _client.Ready += ReadyAsync;
        _client.SlashCommandExecuted += SlashCommandExecutedAsync;

        // Login and start
        await _client.LoginAsync(TokenType.Bot, _botToken);
        await _client.StartAsync();

        Console.WriteLine("🚀 Bot is starting...");

        // Keep running
        await Task.Delay(Timeout.Infinite);
    }

    private static Task LogAsync(LogMessage msg)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {msg}");
        return Task.CompletedTask;
    }

    private static async Task ReadyAsync()
    {
        Console.WriteLine($"✅ Bot is online as {_client?.CurrentUser}");
        await RegisterCommandsAsync();
    }

    private static async Task RegisterCommandsAsync()
    {
        if (_client == null) return;

        var commands = new[]
        {
            new SlashCommandBuilder()
                .WithName("export")
                .WithDescription("Export channel messages")
                .AddOption("limit", ApplicationCommandOptionType.Integer,
                    "Number of messages (default: 100)", false)
                .AddOption("format", ApplicationCommandOptionType.String,
                    "Export format: html, txt, json, csv", false,
                    choices: new[]
                    {
                        new ApplicationCommandOptionChoiceProperties { Name = "HTML", Value = "html" },
                        new ApplicationCommandOptionChoiceProperties { Name = "Text", Value = "txt" },
                        new ApplicationCommandOptionChoiceProperties { Name = "JSON", Value = "json" },
                        new ApplicationCommandOptionChoiceProperties { Name = "CSV", Value = "csv" }
                    })
                .Build(),

            new SlashCommandBuilder()
                .WithName("export-help")
                .WithDescription("Show help")
                .Build()
        };

        try
        {
            foreach (var command in commands)
            {
                await _client.CreateGlobalApplicationCommandAsync(command);
            }
            Console.WriteLine("✅ Commands registered!");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Error registering commands: {ex.Message}");
        }
    }

    private static async Task SlashCommandExecutedAsync(SocketSlashCommand command)
    {
        try
        {
            switch (command.Data.Name)
            {
                case "export":
                    await HandleExportCommand(command);
                    break;
                case "export-help":
                    await HandleHelpCommand(command);
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex}");
            await command.RespondAsync($"Error: {ex.Message}", ephemeral: true);
        }
    }

    private static async Task HandleExportCommand(SocketSlashCommand command)
    {
        await command.DeferAsync();

        var limit = (long)(command.Data.Options.FirstOrDefault(o => o.Name == "limit")?.Value ?? 100L);
        var format = command.Data.Options.FirstOrDefault(o => o.Name == "format")?.Value as string ?? "html";

        try
        {
            var channel = command.Channel;
            var guild = (command.Channel as SocketGuildChannel)?.Guild;

            Console.WriteLine($"Exporting {limit} messages from #{channel.Name}");

            var fileName = await ExportChannelAsync(
                channel.Id.ToString(),
                guild?.Name ?? "DirectMessage",
                channel.Name ?? "unknown",
                format,
                (int)limit
            );

            if (File.Exists(fileName))
            {
                var fileInfo = new FileInfo(fileName);
                if (fileInfo.Length < 25 * 1024 * 1024)
                {
                    await command.FollowupWithFileAsync(
                        fileName,
                        $"✅ Exported {limit} messages as {format.ToUpper()}");
                }
                else
                {
                    await command.FollowupAsync(
                        $"❌ File too large ({fileInfo.Length / 1024 / 1024:F2} MB)");
                }

                try { File.Delete(fileName); } catch { }
            }
        }
        catch (Exception ex)
        {
            await command.FollowupAsync($"❌ Export failed: {ex.Message}");
        }
    }

    private static async Task HandleHelpCommand(SocketSlashCommand command)
    {
        var embed = new EmbedBuilder()
            .WithTitle("Discord Export Bot")
            .WithColor(Color.Blue)
            .WithDescription("Export Discord messages to files")
            .AddField("/export",
                "Options:\n" +
                "• `limit` - Number of messages\n" +
                "• `format` - html, txt, json, csv")
            .WithFooter("Powered by DiscordChatExporter")
            .Build();

        await command.RespondAsync(embed: embed, ephemeral: true);
    }

    private static async Task<string> ExportChannelAsync(
        string channelId,
        string guildName,
        string channelName,
        string format,
        int limit)
    {
        // Simplified export - fetches recent messages and saves to file
        var channel = _client?.GetChannel(ulong.Parse(channelId)) as IMessageChannel;
        if (channel == null) return "";

        var messages = await channel.GetMessagesAsync(limit).FlattenAsync();
        var fileName = Path.Combine(Path.GetTempPath(), $"{channelName}-{DateTime.Now:yyyyMMdd-HHmmss}.{GetFileExtension(format)}");

        using (var writer = new StreamWriter(fileName))
        {
            if (format == "html")
            {
                await writer.WriteLineAsync("<html><body><h1>" + channelName + "</h1>");
                foreach (var msg in messages.Reverse())
                {
                    await writer.WriteLineAsync($"<p><b>{msg.Author.Username}</b> ({msg.Timestamp}): {msg.Content}</p>");
                }
                await writer.WriteLineAsync("</body></html>");
            }
            else if (format == "txt")
            {
                foreach (var msg in messages.Reverse())
                {
                    await writer.WriteLineAsync($"[{msg.Timestamp}] {msg.Author.Username}: {msg.Content}");
                }
            }
            else if (format == "json")
            {
                await writer.WriteLineAsync("[");
                var messageList = messages.Reverse().ToList();
                for (int i = 0; i < messageList.Count; i++)
                {
                    var msg = messageList[i];
                    await writer.WriteLineAsync($"  {{\"author\": \"{msg.Author.Username}\", \"content\": \"{msg.Content?.Replace("\"", "\\\"")}\", \"timestamp\": \"{msg.Timestamp}\"}}" + (i < messageList.Count - 1 ? "," : ""));
                }
                await writer.WriteLineAsync("]");
            }
            else if (format == "csv")
            {
                await writer.WriteLineAsync("Timestamp,Author,Content");
                foreach (var msg in messages.Reverse())
                {
                    await writer.WriteLineAsync($"\"{msg.Timestamp}\",\"{msg.Author.Username}\",\"{msg.Content?.Replace("\"", "\"\"")}\"");
                }
            }
        }

        return fileName;
    }

    private static string GetFileExtension(string format)
    {
        return format switch
        {
            "txt" => "txt",
            "csv" => "csv",
            "json" => "json",
            _ => "html"
        };
    }
}