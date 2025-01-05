This came about from two things -- A) a friendly comment exchange with @nickcosentino [on LinkedIn](https://www.linkedin.com/posts/nickcosentino_csharp-dotnet-oop-activity-7280100552305221632-h_t9?utm_source=share&utm_medium=member_desktop) about concerns around abstract classes, and B) I wanted to improve the blob backup capability for a friend's Azure site that I help support. There's a lot to dig into when it comes to backup/disaster recovery of Azure blob storage. This project here is really minimal. Proper solutions would involve turning on soft deletes and using [events](https://learn.microsoft.com/en-us/azure/storage/blobs/storage-blob-event-overview) to track blob activity. This project performs a simple scan of blobs that meet some criteria, and downloads local file copies. There's no restore or management capability, and brute force _scanning_ is not very scaleable. But for now it works well enough for my purposes, and demonstrates a use of abstract classes that I wanted to highlight.

The heart of this is abstract class [BackupProcess](https://github.com/adamfoneil/BlobBackup/blob/master/Abstractions/BackupProcess.cs). The idea here is to define some low-level behavior that I expect applies to any Azure blob storage backup process that downloads local files. Leave the details for a specific use case for downstream apps to define. In my example that is [ApplicationImageBackup](https://github.com/adamfoneil/BlobBackup/blob/master/ConsoleApp/ApplicationImageBackup.cs). The unique thing about this is the `GetContainerNamesAsync` method override:

<details>
  <summary>Code</summary>

  ```csharp
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
```
</details>

The point here being that the container names I want to backup are the result of a database query. The container names are derived from real estate listing Ids. For each listing, there's a container for the full size images, paired with a container for the thumbnail images. Using [Dapper](https://github.com/DapperLib/Dapper), I'm querying for listings that have been created or modified within a certain number of days ago.

The low-level `BackupProcess` class doesn't know anything about databases nor any specific business logic that might apply. But it does know, for example, how to [get the blob listing](https://github.com/adamfoneil/BlobBackup/blob/master/Abstractions/BackupProcess.cs#L34) and whether a blob [should be backed up at all](https://github.com/adamfoneil/BlobBackup/blob/master/Abstractions/BackupProcess.cs#L38)
 -- concerns that are common to any backup process.
 
I think the issue Nick rightly has with this is with a statement like "concerns that are common to any..." Correctly predicting "common concerns" in a base class over the life of a project is problematic. As time passes and different use cases emerge, we find that the assumptions of a base class don't apply always. Attempts to retrofit or rework base classes to accomodate more and more one-off cases introduces complexity and fragility -- all in the service of a best practice: _don't repeat yourself_. On this point, I completely agree with Nick that inheritance can be a liability. A great illustration of this is a talk by Dan Abramov "The Wet Codebase" on [YouTube](https://youtu.be/17KCHwOwgms?si=17jMC8X3qxiBZrcB). One reason I love this talk is it touches on the larger issue of the frailty of best practices and the need for developer judgement and mindful trade-offs.

What I push back against in Nick's argument is a blanket rejection of all abstract classes. I would echo Dan Abramov's advice to practice "responsible abstraction." Rather than claim all inheritance is bad, instead use it where you like. But be ready to walk it back. That is, when there's a temptation to add complexity to avoid repeating yourself, remove the base class and inline the abstraction.

Today we're told to prefer composition over inheritance -- indeed that is Nick's argument. This doesn't come naturally to me, but I thought I would explore this a little. I asked GitHub Copilot to propose a refactor of my code to use composition instead of inheritance. (A great use of Copilot IMO.) The gist of its response was this:

```csharp
public class ApplicationImageBackup(
    IOptions<ApplicationImageBackup.Options> options,
    ILogger<ApplicationImageBackup> logger,
    IBackupProcess backupProcess)
{
  /* details omitted to keep this simple */
}
```
The idea here is that `ApplicationImageBackup` trades the base class for an interface dependency `IBackupProcess`. You still need to implement `IBackupProcess` somewhere. In practice it would end up as the original `BackupProcess` base class. Having had a few minutes to ponder this, I see the appeal of this pattern since it gives `ApplicationImageBackup` more freedom to do its own thing. But for now I don't feel moved to adopt it here.
