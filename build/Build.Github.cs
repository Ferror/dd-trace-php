using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Git;
using Octokit;
using Octokit.GraphQL;
using Environment = System.Environment;
using Issue = Octokit.Issue;
using Target = Nuke.Common.Target;
using Logger = Serilog.Log;
using System.Net.Http;
using System.Text;
using Octokit.GraphQL.Model;
using System.Net.Http.Json;

partial class Build
{
    [Parameter("A GitHub token (for use in GitHub Actions)", Name = "GITHUB_TOKEN")]
    readonly string GitHubToken;

    [Parameter("Git repository name", Name = "GITHUB_REPOSITORY_NAME", List = false)]
    readonly string GitHubRepositoryName = "dd-trace-php";

    [Parameter("The Pull Request number for GitHub Actions")]
    readonly int? PullRequestNumber;

    [Parameter("The git branch to use", List = false)]
    readonly string TargetBranch;

    const string GitHubRepositoryOwner = "DataDog";

    Target SummaryOfSnapshotChanges => _ => _
           .Unlisted()
           .Requires(() => GitHubRepositoryName)
           .Requires(() => GitHubToken)
           .Requires(() => PullRequestNumber)
           .Requires(() => TargetBranch)
           .Executes(async() =>
            {
                // This assumes that we're running in a pull request, so we compare against the target branch
                var baseCommit = GitTasks.Git($"merge-base origin/{TargetBranch} HEAD").First().Text;

                // This is a dumb implementation that just show the diff
                // We could imagine getting the whole context with -U1000 and show the differences including parent name
                // eg now we show -oldAttribute: oldValue, but we could show -tag.oldattribute: oldvalue
                var changes = GitTasks.Git($"diff --diff-filter=M \"{baseCommit}\" -- */*snapshots*/*.*")
                                   .Select(f => f.Text);

                if (!changes.Any())
                {
                    Console.WriteLine($"No snapshots modified (some may have been added/deleted). Not doing snapshots diff.");
                    return;
                }

                const string unlinkedLinesExplicitor = "[...]";
                var crossVersionTestsNamePattern = new [] {"VersionMismatchNewerNugetTests"};
                var diffCounts = new Dictionary<string, int>();
                StringBuilder diffsInFile = new();
                var considerUpdatingPublicFeed = false;
                var lastLine = string.Empty;
                foreach (var line in changes)
                {
                    if (line.StartsWith("@@ ")) // new change, not looking at files cause the changes would be too different
                    {
                        RecordChange(diffsInFile, diffCounts);
                        lastLine = String.Empty;
                        continue;
                    }

                    if (line.StartsWith("- ") || line.StartsWith("+ "))
                    {
                        if (!string.IsNullOrEmpty(lastLine) &&
                            lastLine[0] != line[0] &&
                            lastLine.Trim(',').Substring(1) == line.Trim(',').Substring(1))
                        {
                            // The two lines are actually the same, just an additional comma on previous line
                            // So we can remove it from the diff for better understanding?
                            diffsInFile.Remove(diffsInFile.Length - lastLine.Length - Environment.NewLine.Length, lastLine.Length + Environment.NewLine.Length);
                            lastLine = string.Empty;
                            continue;
                        }

                        diffsInFile.AppendLine(line);
                        lastLine = line;
                        continue;
                    }

                    if (!string.IsNullOrEmpty(lastLine))
                    {
                        diffsInFile.AppendLine(unlinkedLinesExplicitor);
                        lastLine = string.Empty;
                    }

                    if (!considerUpdatingPublicFeed && crossVersionTestsNamePattern.Any(p => line.Contains(p)))
                    {
                        considerUpdatingPublicFeed = true;
                    }
                }

                RecordChange(diffsInFile, diffCounts);

                var markdown = new StringBuilder();
                markdown.AppendLine("## Snapshots difference summary").AppendLine();
                markdown.AppendLine("The following differences have been observed in committed snapshots. It is meant to help the reviewer.");
                markdown.AppendLine("The diff is simplistic, so please check some files anyway while we improve it.").AppendLine();

                if (considerUpdatingPublicFeed)
                {
                    markdown.AppendLine("**Note** that this PR updates a version mismatch test. You may need to upgrade your code in the Azure public feed");
                }

                foreach (var diff in diffCounts)
                {
                    markdown.AppendLine($"{diff.Value} occurrences of : ");
                    markdown.AppendLine("```diff");
                    markdown.AppendLine(diff.Key);
                    markdown.Append("```").AppendLine();
                }

                await HideCommentsInPullRequest(PullRequestNumber.Value, "## Snapshots difference");
                await PostCommentToPullRequest(PullRequestNumber.Value, markdown.ToString());

                void RecordChange(StringBuilder diffsInFile, Dictionary<string, int> diffCounts)
                {
                    var unlinkedLinesExplicitorWithNewLine = unlinkedLinesExplicitor + Environment.NewLine;

                    if (diffsInFile.Length > 0)
                    {
                        var change = diffsInFile.ToString();
                        if (change.EndsWith(unlinkedLinesExplicitorWithNewLine))
                        {
                            change = change.Substring(0, change.Length - unlinkedLinesExplicitorWithNewLine.Length);
                        }
                        diffCounts.TryAdd(change, 0);
                        diffCounts[change]++;
                        diffsInFile.Clear();
                    }
                }
            });

    async Task PostCommentToPullRequest(int prNumber, string markdown)
    {
        Console.WriteLine("Posting comment to GitHub");

        // post directly to GitHub as
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
        httpClient.DefaultRequestHeaders.Add("Authorization", $"token {GitHubToken}");
        httpClient.DefaultRequestHeaders.UserAgent.Add(new(new System.Net.Http.Headers.ProductHeaderValue("nuke-ci-client")));

        var url = $"https://api.github.com/repos/{GitHubRepositoryOwner}/{GitHubRepositoryName}/issues/{prNumber}/comments";
        Console.WriteLine($"Sending request to '{url}'");

        var result = await httpClient.PostAsJsonAsync(url, new { body = markdown });

        if (result.IsSuccessStatusCode)
        {
            Console.WriteLine("Comment posted successfully");
        }
        else
        {
            var response = await result.Content.ReadAsStringAsync();
            Console.WriteLine("Error: " + response);
            result.EnsureSuccessStatusCode();
        }
    }

    async Task HideCommentsInPullRequest(int prNumber, string prefix)
    {
        try
        {
            Console.WriteLine("Looking for comments to hide in GitHub");

            var clientId = "nuke-ci-client";
            var productInformation = Octokit.GraphQL.ProductHeaderValue.Parse(clientId);
            var connection = new Octokit.GraphQL.Connection(productInformation, GitHubToken);

            var query = new Octokit.GraphQL.Query()
                       .Repository(GitHubRepositoryName, GitHubRepositoryOwner)
                       .PullRequest(prNumber)
                       .Comments()
                       .AllPages()
                       .Select(issue => new { issue.Id, issue.Body, issue.IsMinimized, });

            var issueComments =  (await connection.Run(query)).ToList();

            Console.WriteLine($"Found {issueComments} comments for PR {prNumber}");

            var count = 0;
            foreach (var issueComment in issueComments)
            {
                if (issueComment.IsMinimized || ! issueComment.Body.StartsWith(prefix))
                {
                    continue;
                }

                try
                {
                    var arg = new MinimizeCommentInput
                    {
                        Classifier = ReportedContentClassifiers.Outdated,
                        SubjectId = issueComment.Id,
                        ClientMutationId = clientId
                    };

                    var mutation = new Mutation()
                                  .MinimizeComment(arg)
                                  .Select(x => new { x.MinimizedComment.IsMinimized });

                    await connection.Run(mutation);
                    count++;

                }
                catch (Exception ex)
                {
                    Logger.Warning($"Error minimising comment with ID {issueComment.Id}: {ex}");
                }
            }

            Console.WriteLine($"Minimised {count} comments for PR {prNumber}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"There was an error trying to minimise old comments with prefix '{prefix}': {ex}");
        }
    }
}
