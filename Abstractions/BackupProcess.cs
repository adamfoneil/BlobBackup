using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Abstractions;

public abstract class BackupProcess(
	string connectionString, 
	string localPath,
	ILogger<BackupProcess> logger)
{
	private readonly string _connectionString = connectionString;
	private readonly string _localPath = localPath;
	private readonly ILogger<BackupProcess> _logger = logger;

	protected abstract Task<string[]> GetContainerNamesAsync();
	protected virtual BlobTraits BlobTraits => BlobTraits.None;
	protected virtual BlobStates BlobStates => BlobStates.None;
	protected virtual string? BlobPrefix => default;

	protected virtual (bool Result, string? ExitReason) ShouldContinue => (true, default);

	public async Task<Result> ExecuteAsync(CancellationToken cancellationToken)
	{
		List<Result.File> downloads = [];
		List<Result.Error> errors = [];

		_logger.LogDebug("Getting containers...");
		var containers = await GetContainerNamesAsync();

		var fileLog = await GetFileLogAsync();

		foreach (var container in containers)
		{
			_logger.LogInformation("Processing container {Container}...", container);
		
			var containerClient = new BlobContainerClient(_connectionString, container);
			await foreach (var item in containerClient.GetBlobsAsync(BlobTraits, BlobStates, BlobPrefix, cancellationToken))
			{
				var localFile = Path.Combine(_localPath, container, item.Name);				
				
				var (shouldBackup, exists, reason, timestamp) = ShouldBackup(fileLog, localFile, item.Properties.LastModified);

				if (!shouldBackup)
				{					
					_logger.LogDebug("Skipping {BlobName} because {reason}...", item.Name, reason);
					continue;
				}

				try
				{
					if (cancellationToken.IsCancellationRequested)
					{
						_logger.LogDebug("Cancellation requested...");
						break;
					}

					if (exists)
					{
						_logger.LogDebug("Deleting {LocalFile}...", localFile);
						File.Delete(localFile);
					}

					_logger.LogDebug("Downloading {BlobName}...", item.Name);
					var blobClient = new BlobClient(_connectionString, container, item.Name);

					EnsurePathExists(localFile);

					await blobClient.DownloadToAsync(localFile, cancellationToken);
					File.SetLastWriteTimeUtc(localFile, timestamp);
					downloads.Add(new Result.File() { LocalFile = localFile, Timestamp = timestamp });

					var (shouldContinue, exitReason) = ShouldContinue;

					if (!shouldContinue)
					{
						_logger.LogDebug("Exit condition reached: {reason}", exitReason);
						break;
					}
				}
				catch (Exception exc)
				{
					_logger.LogError(exc, "Error downloading {BlobName}", item.Name);
					errors.Add(new Result.Error() { BlobName = item.Name, Message = exc.Message });
				}
			}
		}

		return new()
		{ 
			Containers = containers, 
			Downloads = [.. downloads],
			Errors = [.. errors]
		};
	}

	private async Task<Dictionary<string, DateTime>> GetFileLogAsync()
	{
		List<Result> pastResults = [];
		var filenames = Directory.GetFiles(_localPath, "result-*.json");

		if (!filenames.Any())
		{
			filenames = [ await RebuildFileLogAsync() ];
		}

		foreach (var filename in filenames) 
		{
			var json = File.ReadAllText(filename);
			var result = JsonSerializer.Deserialize<Result>(json) ?? throw new Exception("couldn't deserialize");
			pastResults.Add(result);
		}

		var allResults = pastResults.SelectMany(result => result.Downloads).ToArray();

		return allResults
			.GroupBy(item => item.LocalFile)
			.Select(grp => grp.MaxBy(item => item.Timestamp))
			.ToDictionary(item => item.LocalFile, item => item.Timestamp);
	}

	private async Task<string> RebuildFileLogAsync()
	{
		var folders = Directory.GetDirectories(_localPath);
		var files = folders.SelectMany(folder => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)).ToArray();
		var timestamps = files.Select(file => new Result.File() { LocalFile = file, Timestamp = File.GetLastWriteTimeUtc(file) }).ToArray();
		var containers = folders.Select(Path.GetFileName).ToArray()!;

		var result = new Result()
		{ 
			Containers = containers!, 
			Downloads = timestamps,
			Errors = []
		};

		return await SaveResultAsync(result);
	}

	public async Task<string> SaveResultAsync(Result result)
	{
		var filename = ResultFilename(_localPath);
		var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
		await File.WriteAllTextAsync(filename, json);
		return filename;
	}

	public class Result
	{
		public required string[] Containers { get; init; }
		public required File[] Downloads { get; init; }
		public required Error[] Errors { get; init; }

		public class File
		{
			public required string LocalFile { get; init; }
			public required DateTime Timestamp { get; init; }
		}

		public class Error
		{
			public required string BlobName { get; init; }
			public required string Message { get; init; }
		}
	}

	private static void EnsurePathExists(string localFile)
	{
		var folder = Path.GetDirectoryName(localFile) ?? throw new Exception("couldn't get folder name");
		if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
	}

	private static (bool Result, bool Exists, string? Reason, DateTime timestamp) ShouldBackup(Dictionary<string, DateTime> fileLog, string localFile, DateTimeOffset? blobLastModified)
	{
		if (fileLog.TryGetValue(localFile, out var lastModified) && lastModified >= blobLastModified) return (false, true, "log shows local file is latest version", lastModified);

		if (!File.Exists(localFile)) return (true, false, "local file missing", DateTime.UtcNow);

		if (blobLastModified is null) return (true, true, "blob last modified date is null", DateTime.UtcNow);

		var fileLastModified = File.GetLastWriteTimeUtc(localFile);

		if (blobLastModified.Value.UtcDateTime > fileLastModified) return (true, true, "blob is newer than local file", blobLastModified.Value.UtcDateTime);

		return (false, true, "local file is latest version", DateTime.UtcNow);
	}

	public static string ResultFilename(string path) => NextFilename(path, $"result-{DateTime.Today:yy-MM-dd}", ".json");

	public static string NextFilename(string path, string prefix, string extension)
	{
		var files = Directory.GetFiles(path, $"{prefix}*{extension}");
		var last = files.Select(file => int.Parse(Path.GetFileNameWithoutExtension(file)[prefix.Length..])).Order().LastOrDefault();
		return Path.Combine(path, $"{prefix}-{last + 1}{extension}");
	}
}
