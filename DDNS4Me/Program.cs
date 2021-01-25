using CommandLine;
using CommandLine.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

static bool TryGetAuthConfig(string authConfigPath, out AuthConfig authConfig)
{
	if (!File.Exists(authConfigPath))
	{
		Console.WriteLine("Config file does not exist.");
		authConfig = null;
		return false;
	}

	authConfig = JsonSerializer.Deserialize<AuthConfig>(File.ReadAllText(authConfigPath));
	return true;
}

static async Task<(bool Success, string IpAddress)> LookupPublicIpAsync(HttpClient httpClient, string ipProviderUrl)
{
	using var publicIpEchoResponse = await httpClient.GetAsync(ipProviderUrl);
	if (publicIpEchoResponse.IsSuccessStatusCode)
	{
		var ipAddress = await publicIpEchoResponse.Content.ReadAsStringAsync();
		return (true, ipAddress.Trim());
	}
	return (false, null);
}

static async Task<bool> VerifyCloudflareAuthAsync(HttpClient httpClient, AuthConfig authConfig)
{
	const string CLOUDFLARE_VERIFY_AUTH_URL = "https://api.cloudflare.com/client/v4/user/tokens/verify";
	
	var requestMessage = new HttpRequestMessage(HttpMethod.Get, CLOUDFLARE_VERIFY_AUTH_URL);
	requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authConfig.CloudflareApiToken);
	using var response = await httpClient.SendAsync(requestMessage);
	return response.IsSuccessStatusCode;
}

static async Task<bool> TryAddDnsRecordAsync(HttpClient httpClient, AuthConfig authConfig, string zoneIdentifier, string dnsName, string ipAddress, int timeToLive)
{
	const string CLOUDFLARE_ADD_DNS_URL = "https://api.cloudflare.com/client/v4/zones/{0}/dns_records";

	var requestMessage = new HttpRequestMessage(HttpMethod.Post, string.Format(CLOUDFLARE_ADD_DNS_URL, zoneIdentifier));
	requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authConfig.CloudflareApiToken);
	requestMessage.Content = JsonContent.Create(new
	{
		type = "A",
		name = dnsName,
		content = ipAddress,
		ttl = timeToLive
	});

	using var response = await httpClient.SendAsync(requestMessage);
	return response.IsSuccessStatusCode;
}

static async Task<bool> TryUpdateDnsRecordAsync(HttpClient httpClient, AuthConfig authConfig, string zoneIdentifier, string dnsName, string dnsRecordIdentifier, string ipAddress, int timeToLive)
{
	const string CLOUDFLARE_UPDATE_DNS_URL = "https://api.cloudflare.com/client/v4/zones/{0}/dns_records/{1}";

	var requestMessage = new HttpRequestMessage(HttpMethod.Put, string.Format(CLOUDFLARE_UPDATE_DNS_URL, zoneIdentifier, dnsRecordIdentifier));
	requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authConfig.CloudflareApiToken);
	requestMessage.Content = JsonContent.Create(new
	{
		type = "A",
		name = dnsName,
		content = ipAddress,
		ttl = timeToLive
	});

	using var response = await httpClient.SendAsync(requestMessage);
	return response.IsSuccessStatusCode;
}

static async Task<DnsRecord> FindExistingDnsRecordAsync(HttpClient httpClient, AuthConfig authConfig, string zoneIdentifier, string dnsName)
{
	const string CLOUDFLARE_LOOKUP_DNS_URL = "https://api.cloudflare.com/client/v4/zones/{0}/dns_records?type=A&name={1}";

	var requestMessage = new HttpRequestMessage(HttpMethod.Get, string.Format(CLOUDFLARE_LOOKUP_DNS_URL, zoneIdentifier, dnsName));
	requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authConfig.CloudflareApiToken);
	using var response = await httpClient.SendAsync(requestMessage);
	var result = await response.Content.ReadFromJsonAsync<ListDnsRecordsResult>();
	
	if (result.Success && result.Result.Length > 0)
	{
		return result.Result[0];
	}

	return null;
}

return await Parser.Default.ParseArguments<UpdateOptions>(args)
	.MapResult(async options =>
	{
		var httpClient = new HttpClient();
		if (TryGetAuthConfig(options.AuthConfigPath, out var authConfig))
		{
			if (await VerifyCloudflareAuthAsync(httpClient, authConfig))
			{
				var publicIpResult = await LookupPublicIpAsync(httpClient, options.IpProviderUrl);
				if (publicIpResult.Success)
				{
					Console.WriteLine("Your IP Address is {0}.", publicIpResult.IpAddress);

					var existingDnsRecord = await FindExistingDnsRecordAsync(httpClient, authConfig, options.ZoneIdentifier, options.DnsName);
					if (existingDnsRecord == null)
					{
						if (await TryAddDnsRecordAsync(httpClient, authConfig, options.ZoneIdentifier, options.DnsName, publicIpResult.IpAddress, options.TimeToLive))
						{
							Console.WriteLine("New DNS record has been created.");
							return 0;
						}
					}
					else
					{
						if (existingDnsRecord.Content == publicIpResult.IpAddress && existingDnsRecord.TimeToLive == options.TimeToLive)
						{
							Console.WriteLine("Existing DNS record is already up-to-date.");
							return 0;
						}
						else if (await TryUpdateDnsRecordAsync(httpClient, authConfig, options.ZoneIdentifier, options.DnsName, existingDnsRecord.Identifier, publicIpResult.IpAddress, options.TimeToLive))
						{
							Console.WriteLine("Existing DNS record has been updated.");
							return 0;
						}
					}

					Console.WriteLine("Failed to add or update DNS record. Check that the DNS name is valid.");
				}
				else
				{
					Console.WriteLine("Failed to lookup public IP address.");
				}
			}
			else
			{
				Console.WriteLine("Authentication failure with Cloudflare. Please check your API token.");
			}
		}

		return 1;
	},	_ => Task.FromResult(1));

class UpdateOptions
{
	[Option("config-path", Required = true, HelpText = "Path to the configuration file that contains your Cloudflare API Token.")]
	public string AuthConfigPath { get; set; }
	[Option("zone", Required = true, HelpText = "The Cloudflare Zone Identifier.")]
	public string ZoneIdentifier { get; set; }
	[Option("name", Required = true, HelpText = "The DNS name to use in the zone.")]
	public string DnsName { get; set; }
	[Option("ttl", Required = false, HelpText = "The DNS record Time-to-Live (TTL) in seconds.", Default = 120)]
	public int TimeToLive { get; set; }
	[Option("ip-provider", Required = false, HelpText = "URL for service that returns your public IP as plain text.", Default = "https://icanhazip.com/")]
	public string IpProviderUrl { get; set; }

	[Usage(ApplicationAlias = "ddns4me")]
	public static IEnumerable<Example> Examples => new[]
	{
		new Example("Common", new UpdateOptions 
		{
			AuthConfigPath = "~/authconfig.json",
			ZoneIdentifier = "C6tbxEC4nDBzP6sKYJ7gdnvyQXi3VKDq",
			DnsName = "test.example.org"
		}),
		new Example("Using a custom IP provider", new UpdateOptions
		{
			AuthConfigPath = "~/authconfig.json",
			ZoneIdentifier = "C6tbxEC4nDBzP6sKYJ7gdnvyQXi3VKDq",
			DnsName = "test.example.org",
			IpProviderUrl = "https://echo.example.org"
		}),
		new Example("Using a custom TTL", new UpdateOptions
		{
			AuthConfigPath = "~/authconfig.json",
			ZoneIdentifier = "C6tbxEC4nDBzP6sKYJ7gdnvyQXi3VKDq",
			DnsName = "test.example.org",
			TimeToLive = 360
		})
	};
}

class AuthConfig
{
	public string CloudflareApiToken { get; set; }
}

class DnsRecord
{
	[JsonPropertyName("id")]
	public string Identifier { get; set; }
	[JsonPropertyName("name")]
	public string Name { get; set; }
	[JsonPropertyName("content")]
	public string Content { get; set; }
	[JsonPropertyName("ttl")]
	public int TimeToLive { get; set; }
}

class ListDnsRecordsResult
{
	[JsonPropertyName("success")]
	public bool Success { get; set; }
	[JsonPropertyName("result")]
	public DnsRecord[] Result { get; set; }
}