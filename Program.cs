using Discord;
using Discord.WebSocket;
using DiscordChatExporter.Core.Discord;
using DiscordChatExporter.Core.Discord.Data;
using DiscordChatExporter.Core.Exporting;
using DiscordChatExporter.Core.Exporting.Filtering;
using DiscordChatExporter.Core.Exporting.Partitioning;
using Gress;
using Microsoft.Extensions.Configuration;
using System;
using System.IO;
using System.Linq;
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
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddEnvironmentVariables()
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
        var formatStr = command.Data.Options.FirstOrDefault(o => o.Name == "format")?.Value as string ?? "html";

        var format = formatStr.ToLower() switch
        {
            "txt" => ExportFormat.PlainText,
            "json" => ExportFormat.Json,
            "csv" => ExportFormat.Csv,
            _ => ExportFormat.HtmlDark
        };

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
                        $"✅ Exported {limit} messages as {formatStr.ToUpper()}");
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
        ExportFormat format,
        int limit)
    {
        var discordClient = new DiscordChatExporter.Core.Discord.DiscordClient(_botToken!);

        var exportRequest = new ExportRequest(
            Guild.DirectMessages,
            new Channel(
                Snowflake.Parse(channelId),
                ChannelKind.GuildTextChat,
                Snowflake.Zero,
                null,
                channelName,
                null,
                null,
                null,
                false,
                null
            ),
            Path.GetTempPath(),
            null,
            format,
            null,
            null,
            PartitionLimit.Null,
            MessageFilter.Null,
            true,
            false,
            false,
            "en-US",
            false
        );

        var exporter = new ChannelExporter(discordClient);
        var progress = new Progress<Percentage>();
        await exporter.ExportChannelAsync(exportRequest, progress);

        var pattern = $"{channelName}*.{GetFileExtension(format)}";
        var files = Directory.GetFiles(Path.GetTempPath(), pattern)
            .OrderByDescending(f => File.GetCreationTime(f))
            .FirstOrDefault();

        return files ?? "";
    }

    private static string GetFileExtension(ExportFormat format)
    {
        return format switch
        {
            ExportFormat.PlainText => "txt",
            ExportFormat.Csv => "csv",
            ExportFormat.Json => "json",
            _ => "html"
        };
    }
}