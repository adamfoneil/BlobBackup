using Abstractions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ConsoleApp;

internal class ApplicationImageBackup(
	IOptions<ApplicationImageBackup.Options> options, 
	ILogger<ApplicationImageBackup> logger) : BackupProcess(options.Value.StorageConnectionString, options.Value.LocalPath, logger)
{
	internal class Options
	{
		public string StorageConnectionString { get; set; } = default!;
		public string DatabaseConnectionString { get; set; } = default!;
		public int DaysBack { get; set; } = 7;
		public string LocalPath { get; set; } = default!;
	}

	private readonly Options _options = options.Value;

	protected override async Task<string[]> GetContainerNamesAsync()
	{
		using var cn = new SqlConnection(_options.DatabaseConnectionString);

		var results = await cn.QueryAsync<int>(
			@"SELECT 
				[Id]
			FROM 
				[dbo].[Listing]
			WHERE 
				DATEDIFF(d, COALESCE([DateModified], [DateCreated]), getdate()) <= @daysBack", new { daysBack = _options.DaysBack });

		return results.SelectMany(id => new string[] { $"listing-{id}", $"listing-{id}-thumb" }).ToArray();
	}
}
