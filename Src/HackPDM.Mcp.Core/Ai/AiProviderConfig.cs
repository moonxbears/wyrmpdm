using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using HackPDM.Shared.GlobalData;

namespace HackPDM.Mcp.Ai;

/// <summary>Supported in-app AI providers for the HackPDM Command Palette &amp; AI Assistant.</summary>
[JsonConverter( typeof( JsonStringEnumConverter ) )]
public enum AiProviderKind
{
	OpenRouter,
	OpenAI,
	Anthropic,
	GoogleGemini,
	GitHubModels,
	Ollama,
	LmStudio,
	CustomOpenAi,
}

/// <summary>Static metadata describing an AI provider, its login flow, defaults, and env vars.</summary>
public sealed record AiProviderDescriptor(
	AiProviderKind Kind,
	string Id,
	string DisplayName,
	string ShortTag,
	string Description,
	string DefaultBaseUrl,
	string DefaultModel,
	IReadOnlyList<string> SuggestedModels,
	bool RequiresApiKey,
	bool SupportsBrowserOAuth,
	string? SignInUrl,
	string? SignInButtonLabel,
	IReadOnlyList<string> EnvVarNames );

/// <summary>Persisted connection profile for one AI provider.</summary>
public sealed class AiProviderProfile
{
	public AiProviderKind Kind { get; set; }
	public string? ApiKey { get; set; }
	public string? BaseUrl { get; set; }
	public string? Model { get; set; }
	public string? AccountLabel { get; set; }
	public string? AuthMethod { get; set; }
	public DateTimeOffset? ConnectedAtUtc { get; set; }

	[JsonIgnore]
	public bool HasCredentials
	{
		get
		{
			AiProviderDescriptor desc = AiProviderCatalog.Get( Kind );
			if( !desc.RequiresApiKey )
				return !string.IsNullOrWhiteSpace( BaseUrl ?? desc.DefaultBaseUrl );
			return !string.IsNullOrWhiteSpace( ApiKey );
		}
	}

	[JsonIgnore]
	public string MaskedApiKey
	{
		get
		{
			if( string.IsNullOrWhiteSpace( ApiKey ) )
				return "(not set)";
			if( ApiKey.Length <= 10 )
				return new string( '•', ApiKey.Length );
			return $"{ApiKey[ ..6 ]}••••••••{ApiKey[ ^4.. ]}";
		}
	}
}

/// <summary>Serialized root document stored at <c>&lt;AppDataPaths.Config&gt;/mcp-ai-providers.json</c>.</summary>
public sealed class AiProviderConfigFile
{
	public int SchemaVersion { get; set; } = 1;
	public AiProviderKind? ActiveProvider { get; set; }
	public Dictionary<string, AiProviderProfile> Profiles { get; set; } = new( StringComparer.OrdinalIgnoreCase );
}

/// <summary>Auto-detected local AI server or environment credential discovered on this machine.</summary>
public sealed record DetectedAiProvider(
	AiProviderKind Kind,
	string DisplayName,
	string Source,
	string? BaseUrl,
	string? SuggestedModel,
	IReadOnlyList<string> AvailableModels,
	string? DetectedApiKey );

/// <summary>Catalog of all 8 built-in AI providers with defaults, auth URLs, and env-var hints.</summary>
public static class AiProviderCatalog
{
	public static IReadOnlyList<AiProviderDescriptor> All { get; } =
	[
		new(
			AiProviderKind.OpenRouter,
			"openrouter",
			"OpenRouter (1-Click Browser Login)",
			"OPENROUTER",
			"Sign in directly in your browser (Google / GitHub / Email) for instant access to Claude, GPT-4o, Gemini, DeepSeek, and Llama with zero manual key copying.",
			"https://openrouter.ai/api/v1",
			"anthropic/claude-sonnet-4",
			[ "anthropic/claude-sonnet-4", "openai/gpt-4o", "google/gemini-2.5-pro-preview", "google/gemini-2.5-flash", "deepseek/deepseek-chat-v3-0324" ],
			RequiresApiKey: true,
			SupportsBrowserOAuth: true,
			SignInUrl: "https://openrouter.ai/keys",
			SignInButtonLabel: "Sign In with Browser (OAuth)",
			EnvVarNames: [ "OPENROUTER_API_KEY" ] ),

		new(
			AiProviderKind.OpenAI,
			"openai",
			"OpenAI (ChatGPT / GPT-4o)",
			"OPENAI",
			"Connect directly to OpenAI models (GPT-4.1, GPT-4o, o4-mini) with full native MCP function calling.",
			"https://api.openai.com/v1",
			"gpt-4.1",
			[ "gpt-4.1", "gpt-4o", "gpt-4o-mini", "o4-mini" ],
			RequiresApiKey: true,
			SupportsBrowserOAuth: false,
			SignInUrl: "https://platform.openai.com/api-keys",
			SignInButtonLabel: "Open OpenAI Sign-In & Keys",
			EnvVarNames: [ "OPENAI_API_KEY" ] ),

		new(
			AiProviderKind.Anthropic,
			"anthropic",
			"Anthropic (Claude)",
			"CLAUDE",
			"Connect directly to Anthropic Claude (Sonnet 4, 3.7 Sonnet, Haiku) via the native Anthropic Messages API with MCP tools.",
			"https://api.anthropic.com",
			"claude-sonnet-4-20250514",
			[ "claude-sonnet-4-20250514", "claude-3-7-sonnet-20250219", "claude-3-5-haiku-20241022" ],
			RequiresApiKey: true,
			SupportsBrowserOAuth: false,
			SignInUrl: "https://console.anthropic.com/settings/keys",
			SignInButtonLabel: "Open Anthropic Console",
			EnvVarNames: [ "ANTHROPIC_API_KEY" ] ),

		new(
			AiProviderKind.GoogleGemini,
			"gemini",
			"Google Gemini",
			"GEMINI",
			"Connect directly to Google Gemini (2.5 Flash / 2.5 Pro) via Google AI Studio with native MCP function calling.",
			"https://generativelanguage.googleapis.com",
			"gemini-2.5-flash",
			[ "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.0-flash" ],
			RequiresApiKey: true,
			SupportsBrowserOAuth: false,
			SignInUrl: "https://aistudio.google.com/apikey",
			SignInButtonLabel: "Open Google AI Studio",
			EnvVarNames: [ "GEMINI_API_KEY", "GOOGLE_API_KEY" ] ),

		new(
			AiProviderKind.GitHubModels,
			"github",
			"GitHub Models / Copilot",
			"GITHUB",
			"Use your GitHub account token with GitHub Models (GPT-4o, o3-mini, Llama, DeepSeek) via models.inference.ai.azure.com.",
			"https://models.inference.ai.azure.com",
			"gpt-4o",
			[ "gpt-4o", "gpt-4o-mini", "DeepSeek-R1", "Llama-3.3-70B-Instruct" ],
			RequiresApiKey: true,
			SupportsBrowserOAuth: false,
			SignInUrl: "https://github.com/settings/tokens",
			SignInButtonLabel: "Open GitHub Token Settings",
			EnvVarNames: [ "GITHUB_TOKEN", "GH_TOKEN" ] ),

		new(
			AiProviderKind.Ollama,
			"ollama",
			"Ollama (Local AI — Free)",
			"OLLAMA",
			"Run 100% private local models on your own computer via Ollama (no account or API key required; works seamlessly in local-only mode).",
			"http://127.0.0.1:11434/v1",
			"llama3.2",
			[ "llama3.2", "qwen2.5-coder:7b", "mistral", "gemma3" ],
			RequiresApiKey: false,
			SupportsBrowserOAuth: false,
			SignInUrl: "https://ollama.com/download",
			SignInButtonLabel: "Get Ollama",
			EnvVarNames: [] ),

		new(
			AiProviderKind.LmStudio,
			"lmstudio",
			"LM Studio (Local AI)",
			"LM STUDIO",
			"Connect to a local LM Studio OpenAI-compatible server on 127.0.0.1:1234 with zero login.",
			"http://127.0.0.1:1234/v1",
			"local-model",
			[ "local-model" ],
			RequiresApiKey: false,
			SupportsBrowserOAuth: false,
			SignInUrl: "https://lmstudio.ai/",
			SignInButtonLabel: "Get LM Studio",
			EnvVarNames: [] ),

		new(
			AiProviderKind.CustomOpenAi,
			"custom",
			"Custom OpenAI-Compatible",
			"CUSTOM",
			"Connect to any OpenAI-compatible endpoint (Azure OpenAI, LiteLLM, vLLM, Groq, Together, Mistral, or self-hosted gateway).",
			"https://api.openai.com/v1",
			"gpt-4o",
			[ "gpt-4o" ],
			RequiresApiKey: false,
			SupportsBrowserOAuth: false,
			SignInUrl: null,
			SignInButtonLabel: null,
			EnvVarNames: [] ),
	];

	public static AiProviderDescriptor Get( AiProviderKind kind )
		=> All.First( d => d.Kind == kind );
}

/// <summary>
/// Thread-safe local store for HackPDM AI provider credentials and auto-detection.
/// Saved with owner-only file permissions (<c>0600</c> on Unix) under <c>AppDataPaths.Config</c>
/// and never exposed unmasked via MCP tools.
/// </summary>
public sealed class AiProviderStore
{
	private static readonly JsonSerializerOptions JsonOpts = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};

	private readonly object _gate = new();
	private readonly bool _persist;
	private AiProviderConfigFile _config;

	public string ConfigFilePath { get; }

	public event Action? Changed;

	public AiProviderStore( string? configFilePathOverride = null, bool persistChanges = true )
	{
		_persist = persistChanges;
		ConfigFilePath = string.IsNullOrWhiteSpace( configFilePathOverride )
			? Path.Combine( AppDataPaths.Config, "mcp-ai-providers.json" )
			: Path.GetFullPath( configFilePathOverride );
		_config = LoadOrDefault();
	}

	public AiProviderKind? ActiveProvider
	{
		get { lock( _gate ) return _config.ActiveProvider; }
	}

	public AiProviderProfile? GetActiveProfile()
	{
		lock( _gate )
		{
			if( _config.ActiveProvider is not { } kind )
				return null;
			return _config.Profiles.TryGetValue( kind.ToString(), out AiProviderProfile? p ) && p.HasCredentials
				? Clone( p )
				: null;
		}
	}

	public AiProviderProfile GetOrCreateProfile( AiProviderKind kind )
	{
		lock( _gate )
		{
			if( _config.Profiles.TryGetValue( kind.ToString(), out AiProviderProfile? existing ) )
				return Clone( existing );

			AiProviderDescriptor desc = AiProviderCatalog.Get( kind );
			return new AiProviderProfile
			{
				Kind = kind,
				BaseUrl = desc.DefaultBaseUrl,
				Model = desc.DefaultModel,
			};
		}
	}

	public IReadOnlyList<AiProviderProfile> ListConfiguredProfiles()
	{
		lock( _gate )
			return _config.Profiles.Values.Where( p => p.HasCredentials ).Select( Clone ).ToList();
	}

	public void SaveAndActivate( AiProviderProfile profile )
	{
		AiProviderDescriptor desc = AiProviderCatalog.Get( profile.Kind );
		AiProviderProfile clean = new()
		{
			Kind = profile.Kind,
			ApiKey = string.IsNullOrWhiteSpace( profile.ApiKey ) ? null : profile.ApiKey.Trim(),
			BaseUrl = string.IsNullOrWhiteSpace( profile.BaseUrl ) ? desc.DefaultBaseUrl : profile.BaseUrl.Trim().TrimEnd( '/' ),
			Model = string.IsNullOrWhiteSpace( profile.Model ) ? desc.DefaultModel : profile.Model.Trim(),
			AccountLabel = string.IsNullOrWhiteSpace( profile.AccountLabel ) ? desc.DisplayName : profile.AccountLabel.Trim(),
			AuthMethod = string.IsNullOrWhiteSpace( profile.AuthMethod ) ? ( desc.RequiresApiKey ? "api_key" : "local" ) : profile.AuthMethod,
			ConnectedAtUtc = profile.ConnectedAtUtc ?? DateTimeOffset.UtcNow,
		};

		lock( _gate )
		{
			_config.Profiles[ clean.Kind.ToString() ] = clean;
			_config.ActiveProvider = clean.Kind;
			PersistLocked();
		}
		Changed?.Invoke();
	}

	public void SignOut( AiProviderKind kind )
	{
		lock( _gate )
		{
			_config.Profiles.Remove( kind.ToString() );
			if( _config.ActiveProvider == kind )
			{
				AiProviderProfile? next = _config.Profiles.Values.FirstOrDefault( p => p.HasCredentials );
				_config.ActiveProvider = next?.Kind;
			}
			PersistLocked();
		}
		Changed?.Invoke();
	}

	/// <summary>
	/// Probes environment variables (OPENAI_API_KEY, ANTHROPIC_API_KEY, GEMINI_API_KEY, OPENROUTER_API_KEY, GITHUB_TOKEN)
	/// and local AI servers (Ollama on 127.0.0.1:11434, LM Studio on 127.0.0.1:1234) so users can connect in 1 click.
	/// </summary>
	public static async Task<IReadOnlyList<DetectedAiProvider>> AutoDetectAvailableAsync(
		HttpClient? httpClient = null,
		Func<string, string?>? envReader = null,
		CancellationToken cancellationToken = default )
	{
		List<DetectedAiProvider> found = [];
		envReader ??= Environment.GetEnvironmentVariable;

		// 1. Environment variable keys (instant, no network)
		foreach( AiProviderDescriptor desc in AiProviderCatalog.All )
		{
			foreach( string envVar in desc.EnvVarNames )
			{
				string? val = envReader( envVar );
				if( !string.IsNullOrWhiteSpace( val ) )
				{
					found.Add( new DetectedAiProvider(
						desc.Kind,
						desc.DisplayName,
						$"Environment variable {envVar}",
						desc.DefaultBaseUrl,
						desc.DefaultModel,
						desc.SuggestedModels,
						val.Trim() ) );
					break;
				}
			}
		}

		// 2. Local AI servers (fast 900ms loopback probes)
		bool ownsClient = httpClient is null;
		HttpClient client = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMilliseconds( 900 ) };
		try
		{
			// Ollama: GET http://127.0.0.1:11434/api/tags
			try
			{
				using HttpResponseMessage resp = await client.GetAsync( "http://127.0.0.1:11434/api/tags", cancellationToken );
				if( resp.IsSuccessStatusCode )
				{
					string body = await resp.Content.ReadAsStringAsync( cancellationToken );
					List<string> models = ParseOllamaTags( body );
					found.Add( new DetectedAiProvider(
						AiProviderKind.Ollama,
						"Ollama (Local AI — Running)",
						"Running locally at 127.0.0.1:11434",
						"http://127.0.0.1:11434/v1",
						models.FirstOrDefault() ?? "llama3.2",
						models,
						null ) );
				}
			}
			catch
			{
			}

			// LM Studio: GET http://127.0.0.1:1234/v1/models
			try
			{
				using HttpResponseMessage resp = await client.GetAsync( "http://127.0.0.1:1234/v1/models", cancellationToken );
				if( resp.IsSuccessStatusCode )
				{
					string body = await resp.Content.ReadAsStringAsync( cancellationToken );
					List<string> models = ParseOpenAiModelIds( body );
					found.Add( new DetectedAiProvider(
						AiProviderKind.LmStudio,
						"LM Studio (Local AI — Running)",
						"Running locally at 127.0.0.1:1234",
						"http://127.0.0.1:1234/v1",
						models.FirstOrDefault() ?? "local-model",
						models,
						null ) );
				}
			}
			catch
			{
			}
		}
		finally
		{
			if( ownsClient )
				client.Dispose();
		}

		return found;
	}

	internal static List<string> ParseOllamaTags( string json )
	{
		List<string> models = [];
		try
		{
			using JsonDocument doc = JsonDocument.Parse( json );
			if( doc.RootElement.TryGetProperty( "models", out JsonElement arr ) && arr.ValueKind == JsonValueKind.Array )
			{
				foreach( JsonElement item in arr.EnumerateArray() )
				{
					if( item.TryGetProperty( "name", out JsonElement nameEl ) && nameEl.ValueKind == JsonValueKind.String
						&& nameEl.GetString() is { Length: > 0 } name )
					{
						models.Add( name );
					}
				}
			}
		}
		catch
		{
		}
		return models;
	}

	internal static List<string> ParseOpenAiModelIds( string json )
	{
		List<string> models = [];
		try
		{
			using JsonDocument doc = JsonDocument.Parse( json );
			if( doc.RootElement.TryGetProperty( "data", out JsonElement arr ) && arr.ValueKind == JsonValueKind.Array )
			{
				foreach( JsonElement item in arr.EnumerateArray() )
				{
					if( item.TryGetProperty( "id", out JsonElement idEl ) && idEl.ValueKind == JsonValueKind.String
						&& idEl.GetString() is { Length: > 0 } id )
					{
						models.Add( id );
					}
				}
			}
		}
		catch
		{
		}
		return models;
	}

	private AiProviderConfigFile LoadOrDefault()
	{
		try
		{
			if( File.Exists( ConfigFilePath ) )
			{
				string json = File.ReadAllText( ConfigFilePath );
				AiProviderConfigFile? loaded = JsonSerializer.Deserialize<AiProviderConfigFile>( json, JsonOpts );
				if( loaded is not null )
				{
					loaded.Profiles ??= new( StringComparer.OrdinalIgnoreCase );
					return loaded;
				}
			}
		}
		catch
		{
		}
		return new AiProviderConfigFile();
	}

	private void PersistLocked()
	{
		if( !_persist )
			return;

		try
		{
			if( Path.GetDirectoryName( ConfigFilePath ) is { Length: > 0 } dir )
				Directory.CreateDirectory( dir );
			string tmp = ConfigFilePath + ".tmp";
			File.WriteAllText( tmp, JsonSerializer.Serialize( _config, JsonOpts ) );
			if( !RuntimeInformation.IsOSPlatform( OSPlatform.Windows ) )
			{
				try
				{
					File.SetUnixFileMode( tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite );
				}
				catch
				{
				}
			}
			File.Move( tmp, ConfigFilePath, overwrite: true );
		}
		catch
		{
		}
	}

	private static AiProviderProfile Clone( AiProviderProfile p ) => new()
	{
		Kind = p.Kind,
		ApiKey = p.ApiKey,
		BaseUrl = p.BaseUrl,
		Model = p.Model,
		AccountLabel = p.AccountLabel,
		AuthMethod = p.AuthMethod,
		ConnectedAtUtc = p.ConnectedAtUtc,
	};
}
