using System.Text.Json;
using LocalProfanityCensor.DotNet.Models;
using LocalProfanityCensor.DotNet.Services;

namespace LocalProfanityCensor.DotNet.Cli;

internal static class CliApp
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || IsHelpRequest(args[0]))
        {
            WriteUsage();
            return 0;
        }

        var command = args[0];
        var commandArguments = CommandArguments.Parse(args.Skip(1));

        try
        {
            return command switch
            {
                "inspect" => await RunInspectAsync(commandArguments),
                "validate-dictionary" => RunValidateDictionary(commandArguments),
                "transcribe" => await RunTranscribeAsync(commandArguments),
                "compare-transcripts" => await RunCompareTranscriptsAsync(commandArguments),
                "align-transcript" => await RunAlignTranscriptAsync(commandArguments),
                "prepare-replace-prototype" => await RunPrepareReplacePrototypeAsync(commandArguments),
                "synthesize-replace-prototype" => await RunSynthesizeReplacePrototypeAsync(commandArguments),
                "health-check" => RunHealthCheck(commandArguments),
                "process-file" => await RunProcessFileAsync(commandArguments),
                "process" => await RunProcessFolderAsync(commandArguments),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            WriteUsage();
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static async Task<int> RunInspectAsync(CommandArguments args)
    {
        var inputPath = args.RequirePositional(0, "inspect requires <inputPath>.");
        var mediaInfo = await MediaInspector.InspectAsync(inputPath);
        WriteJson(mediaInfo);
        return 0;
    }

    private static int RunValidateDictionary(CommandArguments args)
    {
        var dictionaryPath = args.RequirePositional(0, "validate-dictionary requires <dictionaryPath>.");
        var result = DictionaryService.ValidateDictionary(dictionaryPath);
        if (!result.IsValid)
        {
            Console.Error.WriteLine(result.Message);
            return 1;
        }

        Console.WriteLine(result.Message);
        return 0;
    }

    private static async Task<int> RunTranscribeAsync(CommandArguments args)
    {
        var inputPath = args.RequirePositional(0, "transcribe requires <inputPath>.");
        var outputPath = args.RequireOption("output", "transcribe requires --output <path>.");
        var configPath = args.GetOption("config");
        var showProgress = args.HasFlag("progress");
        var config = ConfigLoader.Load(configPath);

        using var progressScope = showProgress ? ProgressReporter.BeginConsoleReporting() : null;
        ProgressReporter.ReportStage("transcribe", "Starting transcription", fileName: Path.GetFileName(inputPath));
        var transcript = await ProfanityProcessingService.TranscribeAsync(inputPath, config);
        var artifact = TranscriptArtifactService.Build(inputPath, transcript);
        await TranscriptArtifactService.WriteAsync(outputPath, artifact);
        ProgressReporter.ReportStage("transcribe", "Transcript artifact written", fileName: Path.GetFileName(inputPath), detail: outputPath);
        Console.WriteLine($"Wrote transcript to {outputPath}");
        return 0;
    }

    private static async Task<int> RunCompareTranscriptsAsync(CommandArguments args)
    {
        var referencePath = args.RequirePositional(0, "compare-transcripts requires <referenceTranscript> <candidateTranscript>.");
        var candidatePath = args.RequirePositional(1, "compare-transcripts requires <referenceTranscript> <candidateTranscript>.");
        var outputPath = args.RequireOption("output", "compare-transcripts requires --output <path>.");

        var reference = await TranscriptArtifactService.LoadAsync(referencePath);
        var candidate = await TranscriptArtifactService.LoadAsync(candidatePath);
        var comparison = TranscriptComparisonService.Compare(referencePath, reference, candidatePath, candidate);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(comparison, JsonOptions));
        Console.WriteLine($"Wrote transcript comparison to {outputPath}");
        return 0;
    }

    private static async Task<int> RunAlignTranscriptAsync(CommandArguments args)
    {
        var alignmentRequestPath = args.RequirePositional(0, "align-transcript requires <alignmentRequestPath>.");
        var outputPath = args.RequireOption("output", "align-transcript requires --output <path>.");

        var alignmentRequest = await AlignmentRequestService.LoadAsync(alignmentRequestPath);
        var alignedTranscript = AlignmentPrototypeService.Align(alignmentRequest);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(alignedTranscript, JsonOptions));
        Console.WriteLine($"Wrote aligned transcript to {outputPath}");
        return 0;
    }

    private static async Task<int> RunProcessFileAsync(CommandArguments args)
    {
        var inputPath = args.RequirePositional(0, "process-file requires <inputPath> <outputPath>.");
        var outputPath = args.RequirePositional(1, "process-file requires <inputPath> <outputPath>.");
        var dictionaryPath = args.RequireOption("dictionary", "process-file requires --dictionary <path>.");
        var configPath = args.GetOption("config");
        var dryRun = args.HasFlag("dry-run");
        var keepWork = args.HasFlag("keep-work");
        var showProgress = args.HasFlag("progress");
        var mode = args.GetOption("mode");
        var bootstrap = args.HasFlag("bootstrap");
        var noPrompt = args.HasFlag("no-prompt");

        var resolvedConfig = ConfigLoader.LoadRuntime(configPath, dictionaryPath, dryRun, keepWork, mode);
        if (!EnsureRuntimeReady(resolvedConfig, mode, bootstrap, noPrompt))
        {
            return 2;
        }

        using var progressScope = showProgress ? ProgressReporter.BeginConsoleReporting() : null;
        ProgressReporter.ReportStage("process-file", "Starting file processing", fileName: Path.GetFileName(inputPath), mode: resolvedConfig.Censor.Mode);
        var result = await ProfanityProcessingService.ProcessFileAsync(inputPath, outputPath, dictionaryPath, resolvedConfig);
        ProgressReporter.ReportStage("process-file", $"Finished with status {result.Status}", fileName: Path.GetFileName(inputPath), mode: resolvedConfig.Censor.Mode);
        WriteJson(result);
        return result.Status switch
        {
            "dry_run_completed" => 0,
            "completed" => 0,
            "completed_with_warnings" => 0,
            "completed_no_changes" => 0,
            "completed_subtitle_only" => 0,
            "not_implemented" => 2,
            _ => 1,
        };
    }

    private static async Task<int> RunPrepareReplacePrototypeAsync(CommandArguments args)
    {
        var inputPath = args.RequirePositional(0, "prepare-replace-prototype requires <inputPath>.");
        var outputDir = args.RequireOption("output-dir", "prepare-replace-prototype requires --output-dir <path>.");
        var dictionaryPath = args.RequireOption("dictionary", "prepare-replace-prototype requires --dictionary <path>.");
        var configPath = args.GetOption("config");
        var showProgress = args.HasFlag("progress");
        var requestedMatchIndexRaw = args.GetOption("match-index");
        var requestedMatchIndex = int.TryParse(requestedMatchIndexRaw, out var parsedMatchIndex) && parsedMatchIndex > 0
            ? parsedMatchIndex
            : 1;

        var resolvedConfig = ConfigLoader.LoadRuntime(configPath, dictionaryPath, dryRun: true, keepWork: true, mode: "replace");
        using var progressScope = showProgress ? ProgressReporter.BeginConsoleReporting() : null;
        ProgressReporter.ReportStage("prepare-replace", "Preparing replace prototype", fileName: Path.GetFileName(inputPath), mode: "replace");
        var result = await ReplacePrototypeService.PrepareAsync(inputPath, outputDir, dictionaryPath, resolvedConfig, requestedMatchIndex);
        ProgressReporter.ReportStage("prepare-replace", $"Finished with status {result.Status}", fileName: Path.GetFileName(inputPath), mode: "replace");
        WriteJson(result);
        return result.Status == "prototype_prepared" ? 0 : 1;
    }

    private static async Task<int> RunSynthesizeReplacePrototypeAsync(CommandArguments args)
    {
        var manifestPath = args.RequirePositional(0, "synthesize-replace-prototype requires <manifestPath>.");
        var outputDir = args.RequireOption("output-dir", "synthesize-replace-prototype requires --output-dir <path>.");
        var device = args.GetOption("device") ?? "auto";
        var checkpointsDir = args.GetOption("checkpoints-dir");
        var speakerId = args.GetOption("speaker-id") ?? "en-default";
        var language = args.GetOption("language") ?? "EN";
        var showProgress = args.HasFlag("progress");

        using var progressScope = showProgress ? ProgressReporter.BeginConsoleReporting() : null;
        ProgressReporter.ReportStage("synthesize-replace", "Starting OpenVoice synthesis", fileName: Path.GetFileName(manifestPath), mode: "replace");
        var result = await OpenVoicePrototypeService.RunAsync(manifestPath, outputDir, device, checkpointsDir, speakerId, language);
        ProgressReporter.ReportStage("synthesize-replace", $"Finished with status {result.Status}", fileName: Path.GetFileName(manifestPath), mode: "replace");
        WriteJson(result);
        return result.Status == "completed" ? 0 : 1;
    }

    private static async Task<int> RunProcessFolderAsync(CommandArguments args)
    {
        var inputPath = args.RequirePositional(0, "process requires <inputDir> <outputDir>.");
        var outputPath = args.RequirePositional(1, "process requires <inputDir> <outputDir>.");
        var dictionaryPath = args.RequireOption("dictionary", "process requires --dictionary <path>.");
        var configPath = args.GetOption("config");
        var dryRun = args.HasFlag("dry-run");
        var keepWork = args.HasFlag("keep-work");
        var showProgress = args.HasFlag("progress");
        var mode = args.GetOption("mode");
        var bootstrap = args.HasFlag("bootstrap");
        var noPrompt = args.HasFlag("no-prompt");

        var resolvedConfig = ConfigLoader.LoadRuntime(configPath, dictionaryPath, dryRun, keepWork, mode);
        if (!EnsureRuntimeReady(resolvedConfig, mode, bootstrap, noPrompt))
        {
            return 2;
        }

        using var progressScope = showProgress ? ProgressReporter.BeginConsoleReporting() : null;
        ProgressReporter.ReportStage("process-folder", "Starting folder processing", fileName: Path.GetFileName(inputPath), mode: resolvedConfig.Censor.Mode);
        var result = await ProfanityProcessingService.ProcessFolderAsync(inputPath, outputPath, dictionaryPath, resolvedConfig);
        ProgressReporter.ReportStage("process-folder", $"Finished {result.Count} file(s)", fileName: Path.GetFileName(inputPath), mode: resolvedConfig.Censor.Mode);
        WriteJson(result);
        return result.Any(item => item.Status == "failed") ? 1 : 0;
    }

    private static int RunHealthCheck(CommandArguments args)
    {
        var configPath = args.GetOption("config");
        var dictionaryPath = args.GetOption("dictionary");
        var mode = args.GetOption("mode");
        var scope = args.GetOption("scope");
        var config = ConfigLoader.LoadRuntime(configPath, dictionaryPath, dryRun: false, keepWork: false, mode);
        var result = RuntimeReadinessService.Check(config, scope ?? mode ?? config.Censor.Mode);
        WriteJson(result);
        return result.IsReady ? 0 : 2;
    }

    private static bool EnsureRuntimeReady(AppConfig config, string? mode, bool bootstrap, bool noPrompt)
    {
        var readiness = RuntimeReadinessService.Check(config, mode ?? config.Censor.Mode);
        if (readiness.IsReady)
        {
            return true;
        }

        Console.Error.WriteLine(readiness.Message);
        foreach (var item in readiness.MissingItems)
        {
            Console.Error.WriteLine($"  - {item}");
        }

        if (bootstrap)
        {
            return RunBootstrap(readiness);
        }

        if (noPrompt || Console.IsInputRedirected)
        {
            if (!string.IsNullOrWhiteSpace(readiness.BootstrapScript))
            {
                Console.Error.WriteLine($"Run bootstrap script: {readiness.BootstrapScript}");
            }

            return false;
        }

        Console.Error.Write("Run bootstrap now? [Y/N]: ");
        var response = Console.ReadLine();
        if (!string.Equals(response, "y", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(response, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return RunBootstrap(readiness);
    }

    private static bool RunBootstrap(RuntimeReadinessResult readiness)
    {
        if (string.IsNullOrWhiteSpace(readiness.BootstrapScript) || !File.Exists(readiness.BootstrapScript))
        {
            Console.Error.WriteLine("Bootstrap script was not found.");
            return false;
        }

        try
        {
            var shell = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(shell))
            {
                shell = "powershell.exe";
            }

            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{readiness.BootstrapScript}\"",
                UseShellExecute = false,
            });

            process?.WaitForExit();
            return process is not null && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Bootstrap failed: {ex.Message}");
            return false;
        }
    }

    private static void WriteJson<T>(T value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    private static bool IsHelpRequest(string value)
    {
        return value is "help" or "--help" or "-h";
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private static void WriteUsage()
    {
        Console.WriteLine("LocalProfanityCensor.DotNet");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  inspect <inputPath>");
        Console.WriteLine("  validate-dictionary <dictionaryPath>");
        Console.WriteLine("  transcribe <inputPath> --output <path> [--config <path>] [--progress]");
        Console.WriteLine("  compare-transcripts <referenceTranscript> <candidateTranscript> --output <path>");
        Console.WriteLine("  align-transcript <alignmentRequestPath> --output <path>");
        Console.WriteLine();
        Console.WriteLine("Stable commands:");
        Console.WriteLine("  health-check [--config <path>] [--dictionary <path>] [--mode <mute|beep|duck|replace>] [--scope <core|ai|replace>]");
        Console.WriteLine("  process-file <inputPath> <outputPath> --dictionary <path> [--config <path>] [--dry-run] [--keep-work] [--mode <mute|beep|duck|replace>] [--progress] [--bootstrap] [--no-prompt]");
        Console.WriteLine("  process <inputDir> <outputDir> --dictionary <path> [--config <path>] [--dry-run] [--keep-work] [--mode <mute|beep|duck|replace>] [--progress] [--bootstrap] [--no-prompt]");
        Console.WriteLine();
        Console.WriteLine("Experimental prototype commands:");
        Console.WriteLine("  These commands are intended for testing and review. Output must be checked manually and should not be treated as production-ready.");
        Console.WriteLine("  prepare-replace-prototype <inputPath> --output-dir <path> --dictionary <path> [--config <path>] [--match-index <n>] [--progress]");
        Console.WriteLine("  synthesize-replace-prototype <manifestPath> --output-dir <path> [--device <auto|cpu|cuda>] [--checkpoints-dir <path>] [--speaker-id <name>] [--language <EN>] [--progress]");
    }
}