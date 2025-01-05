using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

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
		List<string> downloads = [];
		List<(string, string)> errors = [];

		_logger.LogDebug("Getting containers...");
		var containers = await GetContainerNamesAsync();

		foreach (var container in containers)
		{
			var containerClient = new BlobContainerClient(_connectionString, container);
			await foreach (var item in containerClient.GetBlobsAsync(BlobTraits, BlobStates, BlobPrefix, cancellationToken))
			{
				var localFile = Path.Combine(_localPath, container, item.Name);				

				var (shouldBackup, exists, reason, timestamp) = ShouldBackup(localFile, item.Properties.LastModified);

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
					downloads.Add(localFile);

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
					errors.Add((item.Name, exc.Message));
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

	public class Result
	{
		public required string[] Containers { get; init; }
		public required string[] Downloads { get; init; }
		public required (string, string)[] Errors { get; init; }
	}

	private static void EnsurePathExists(string localFile)
	{
		var folder = Path.GetDirectoryName(localFile) ?? throw new Exception("couldn't get folder name");
		if (!Directory.Exists(folder)) Directory.CreateDirectory(folder);
	}

	private static (bool Result, bool Exists, string? Reason, DateTime timestamp) ShouldBackup(string localFile, DateTimeOffset? blobLastModified)
	{
		if (!File.Exists(localFile)) return (true, false, "local file missing", DateTime.UtcNow);

		if (blobLastModified is null) return (true, true, "blob last modified date is null", DateTime.UtcNow);

		var fileLastModified = File.GetLastWriteTimeUtc(localFile);

		if (blobLastModified.Value.UtcDateTime > fileLastModified) return (true, true, "blob is newer than local file", blobLastModified.Value.UtcDateTime);

		return (false, true, "local file is latest version", DateTime.UtcNow);
	}
}
