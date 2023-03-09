using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using CommunityToolkit.HighPerformance.Buffers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Formatting.Json;

await Host.CreateDefaultBuilder()
	.ConfigureLogging((builderContext, loggingBuilder) => {
		loggingBuilder.AddJsonConsole(jsonOptions => {
			jsonOptions.UseUtcTimestamp = true;
			jsonOptions.TimestampFormat = System.Globalization.DateTimeFormatInfo.InvariantInfo.SortableDateTimePattern;
			jsonOptions.IncludeScopes = true;
			jsonOptions.JsonWriterOptions = new System.Text.Json.JsonWriterOptions() {
				Indented = true
			};
		});
		loggingBuilder.AddConsole(options => {
			// uncomment this line to use the custom formatter below (only if both Serilog lines are commented out)
			//options.FormatterName = "MyFormatter";
		});
		loggingBuilder.AddConsoleFormatter<MyFormatter, JsonConsoleFormatterOptions>(options => {
			options.UseUtcTimestamp = true;
			options.TimestampFormat = System.Globalization.DateTimeFormatInfo.InvariantInfo.SortableDateTimePattern;
			options.IncludeScopes = true;
		});
	})
	.ConfigureServices((_, services) => {
		services.AddSingleton<ConsoleApplication>();
	})

	// uncomment this line to cause serilog to write json to the console
	.UseSerilog((context, services, configuration) => configuration.WriteTo.Console(new JsonFormatter(null, true, null)))

	// uncomment this line to cause serilog to write plain text to the console
	//.UseSerilog((context, services, configuration) => configuration.WriteTo.Console())

	.Build().Services.GetRequiredService<ConsoleApplication>().Run();

class ConsoleApplication
{
	private readonly ILogger<ConsoleApplication> _logger;

	public ConsoleApplication(ILogger<ConsoleApplication> logger)
	{
		_logger = logger;
	}

	public Task<int> Run()
	{
		var state = new[] {
			KeyValuePair.Create<string, object>("state-key-1", "state-value-1"),
			KeyValuePair.Create<string, object>("state-key-2", 2),
		};

		var sprops = new[] {
			KeyValuePair.Create<string, object>("scope-key-1", "scope-value-1"),
			KeyValuePair.Create<string, object>("scope-key-2", 9),
		};

		var lm = LoggerMessage.Define<string>(LogLevel.Error, new EventId(11, "eleven"), "This message came from LoggerMessage.Define; the formatted value is: {formatted_value}");

		using (var scope = _logger.BeginScope(sprops)) {
			lm(_logger, "my-formatted-value", null);
		}

		using (var _ = _logger.BeginScope("a - {a}, b - {b}", "a", 19)) {
			_logger.Log(LogLevel.Error, new EventId(12, "twelve"), state, null, (state, ex) => "This message came from _logger.Log(...); it has no formatted values.");
		}
		
		return Task.FromResult(0);
	}
}

class MyFormatter : ConsoleFormatter, IDisposable
{
	private const string TIMESTAMP     = "Timestamp";
	private const string SCOPES        = "Scopes";
	private const string EVENT_ID_NAME = "EventName";
	private const string MESSAGE       = "Message";

	private readonly IDisposable? _optionsReloadToken;

	private JsonConsoleFormatterOptions _options;

	public MyFormatter(IOptionsMonitor<JsonConsoleFormatterOptions> options) : base(nameof(MyFormatter))
	{
		_options            = options.CurrentValue;
		_optionsReloadToken = options.OnChange(o => _options = o);
	}

	public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
	{
		var message = logEntry.Formatter(logEntry.State, logEntry.Exception);

		using (var output = new ArrayPoolBufferWriter<byte>(1024)) {
			using (var writer = new Utf8JsonWriter(output, _options.JsonWriterOptions)) {
				writer.WriteStartObject();

				if (!string.IsNullOrWhiteSpace(_options.TimestampFormat)) {
					writer.WriteString(TIMESTAMP, (_options.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now).ToString(_options.TimestampFormat));
				}

				writer.WriteNumber(nameof(logEntry.EventId), logEntry.EventId.Id);

				if (!string.IsNullOrWhiteSpace(logEntry.EventId.Name)) {
					writer.WriteString(EVENT_ID_NAME, logEntry.EventId.Name);
				}

				writer.WriteString(nameof(logEntry.LogLevel), logEntry.LogLevel switch {
					LogLevel.Trace       => nameof(LogLevel.Trace),
					LogLevel.Debug       => nameof(LogLevel.Debug),
					LogLevel.Information => nameof(LogLevel.Information),
					LogLevel.Warning     => nameof(LogLevel.Warning),
					LogLevel.Error       => nameof(LogLevel.Error),
					LogLevel.Critical    => nameof(LogLevel.Critical),				
					_ => throw new ArgumentOutOfRangeException(nameof(logEntry.LogLevel))			
					});

				writer.WriteString(nameof(logEntry.Category), logEntry.Category);
				writer.WriteString(MESSAGE, message);

				if (logEntry.Exception != null) {
					var exceptionMessage = logEntry.Exception.ToString();

					if (!_options.JsonWriterOptions.Indented) {
						exceptionMessage = exceptionMessage.Replace(Environment.NewLine, " ");
					}

					writer.WriteString(nameof(logEntry.Exception), exceptionMessage);
  				}

				if (logEntry.State != null) {
					writer.WriteStartObject(nameof(logEntry.State));

					if (logEntry.State is IEnumerable<KeyValuePair<string, object>> stateProperties) {
						WriteItems(writer, stateProperties);
					} else {
						writer.WriteString(MESSAGE, logEntry.State.ToString());
					}

					writer.WriteEndObject();
				}

				if (_options.IncludeScopes && scopeProvider != null) {
					writer.WriteStartArray(SCOPES);

					scopeProvider.ForEachScope((scope, state) => {
						if (scope is IEnumerable<KeyValuePair<string, object>> scopeItems) {
							state.WriteStartObject();
							WriteItems(writer, scopeItems);
							state.WriteEndObject();
						} else {
							state.WriteStringValue(Convert.ToString(scope, CultureInfo.InvariantCulture));
						}
					}, writer);

					writer.WriteEndArray();
				}

				writer.WriteEndObject();
				writer.Flush();
			}

			textWriter.WriteLine(Encoding.UTF8.GetString(output.WrittenMemory.Span));
		}
	}

	private void WriteItems(Utf8JsonWriter writer, IEnumerable<KeyValuePair<string, object>> items)
	{
		foreach (var (key, value) in items) {
			switch (value) {
				case bool boolValue: writer.WriteBoolean(key, boolValue); break;
				case byte byteValue: writer.WriteNumber(key, byteValue); break;
				case sbyte sbyteValue: writer.WriteNumber(key, sbyteValue); break;
				case char charValue: writer.WriteString(key, MemoryMarshal.CreateSpan(ref charValue, 1)); break;
				case decimal decimalValue: writer.WriteNumber(key, decimalValue); break;
				case double doubleValue: writer.WriteNumber(key, doubleValue); break;
				case float floatValue: writer.WriteNumber(key, floatValue); break;
				case int intValue: writer.WriteNumber(key, intValue); break;
				case uint uintValue: writer.WriteNumber(key, uintValue); break;
				case long longValue: writer.WriteNumber(key, longValue); break;
				case ulong ulongValue: writer.WriteNumber(key, ulongValue); break;
				case short shortValue: writer.WriteNumber(key, shortValue); break;
				case ushort ushortValue: writer.WriteNumber(key, ushortValue); break;
				case null: writer.WriteNull(key); break;
				default: writer.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
			}
		}
	}

	public void Dispose() => _optionsReloadToken?.Dispose();
}