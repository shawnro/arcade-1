// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Octokit;

namespace Microsoft.DotNet.DarcLib
{
    public class GitHubClient : IGitRepo
    {
        private const string GitHubApiUri = "https://api.github.com";
        private const string DarcLibVersion = "1.0.0";
        private static ProductHeaderValue _product;
        private readonly string _userAgent = $"DarcLib-{DarcLibVersion}";
        private readonly string _personalAccessToken;
        private readonly ILogger _logger;
        private readonly JsonSerializerSettings _serializerSettings;
        private readonly Lazy<Octokit.GitHubClient> _lazyClient;

        public Octokit.GitHubClient Client => _lazyClient.Value;

        static GitHubClient()
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                .InformationalVersion;
            _product = new ProductHeaderValue("DarcLib", version);
        }

        public GitHubClient(string accessToken, ILogger logger)
        {
            _personalAccessToken = accessToken;
            _logger = logger;
            _serializerSettings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };
            _lazyClient = new Lazy<Octokit.GitHubClient>(CreateGitHubClientClient);
        }

        private Octokit.GitHubClient CreateGitHubClientClient()
        {
            return new Octokit.GitHubClient(_product)
            {
                Credentials = new Credentials(_personalAccessToken)
            };
        }

        public async Task<string> GetFileContentsAsync(string filePath, string repoUri, string branch)
        {
            _logger.LogInformation($"Getting the contents of file '{filePath}' from repo '{repoUri}' in branch '{branch}'...");

            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);

            HttpResponseMessage response;
            try
            {
                response = await this.ExecuteGitCommand(
                    HttpMethod.Get,
                    $"repos/{ownerAndRepo}/contents/{filePath}?ref={branch}",
                    _logger);
            }
            catch (HttpRequestException reqEx) when (reqEx.Message.Contains("404 (Not Found)"))
            {
                throw new DependencyFileNotFoundException(filePath, repoUri, branch, reqEx);
            }

            _logger.LogInformation($"Getting the contents of file '{filePath}' from repo '{repoUri}' in branch '{branch}' succeeded!");

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            string content = responseContent["content"].ToString();

            return this.GetDecodedContent(content);
        }

        public async Task CreateBranchAsync(string repoUri, string newBranch, string baseBranch)
        {
            _logger.LogInformation($"Verifying if '{newBranch}' branch exist in repo '{repoUri}'. If not, we'll create it...");

            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            string latestSha = await GetLastCommitShaAsync(ownerAndRepo, baseBranch);
            string body;

            string gitRef = $"refs/heads/{newBranch}";
            GitHubRef githubRef = new GitHubRef(gitRef, latestSha);
            HttpResponseMessage response = null;

            try
            {
                response = await this.ExecuteGitCommand(HttpMethod.Get, $"repos/{ownerAndRepo}/branches/{newBranch}", _logger);

                githubRef.Force = true;
                body = JsonConvert.SerializeObject(githubRef, _serializerSettings);
                await this.ExecuteGitCommand(
                    new HttpMethod("PATCH"),
                    $"repos/{ownerAndRepo}/git/{gitRef}",
                    _logger,
                    body);
            }
            catch (HttpRequestException exc) when (exc.Message.Contains(((int)HttpStatusCode.NotFound).ToString()))
            {
                _logger.LogInformation($"'{newBranch}' branch doesn't exist. Creating it...");

                body = JsonConvert.SerializeObject(githubRef, _serializerSettings);
                response = await this.ExecuteGitCommand(HttpMethod.Post, $"repos/{ownerAndRepo}/git/refs", _logger, body);

                _logger.LogInformation($"Branch '{newBranch}' created in repo '{repoUri}'!");

                return;
            }
            catch (HttpRequestException exc)
            {
                _logger.LogError($"Checking if '{newBranch}' branch existed in repo '{repoUri}' failed with '{exc.Message}'");

                throw;
            }

            _logger.LogInformation($"Branch '{newBranch}' exists.");
        }

        public async Task PushFilesAsync(List<GitFile> filesToCommit, string repoUri, string branch, string commitMessage)
        {
            using (_logger.BeginScope("Pushing files to {branch}", branch))
            {
                var (owner, repo) = ParseRepoUri(repoUri);

                var baseCommitSha = await Client.Repository.Commit.GetSha1(owner, repo, branch);
                var baseCommit = await Client.Git.Commit.Get(owner, repo, baseCommitSha);
                var baseTree = await Client.Git.Tree.Get(owner, repo, baseCommit.Tree.Sha);
                var newTree = await CreateGitHubTreeAsync(owner, repo, filesToCommit, baseTree);
                var newCommit = await Client.Git.Commit.Create(
                    owner,
                    repo,
                    new NewCommit(commitMessage, newTree.Sha, baseCommit.Sha));
                await Client.Git.Reference.Update(owner, repo, $"heads/{branch}", new ReferenceUpdate(newCommit.Sha));
            }
        }

        public async Task<IEnumerable<int>> SearchPullRequestsAsync(string repoUri, string pullRequestBranch, PrStatus status, string keyword = null, string author = null)
        {
            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            StringBuilder query = new StringBuilder();

            if (!string.IsNullOrEmpty(keyword))
            {
                query.Append(keyword);
                query.Append("+");
            }

            query.Append($"repo:{ownerAndRepo}+head:{pullRequestBranch}+type:pr+is:{status.ToString().ToLower()}");

            if (!string.IsNullOrEmpty(author))
            {
                query.Append($"+author:{author}");
            }

            HttpResponseMessage response = await this.ExecuteGitCommand(HttpMethod.Get, $"search/issues?q={query.ToString()}", _logger);

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());
            JArray items = JArray.Parse(responseContent["items"].ToString());

            IEnumerable<int> prs = items.Select(r => r["number"].ToObject<int>());

            return prs;
        }

        public async Task<PrStatus> GetPullRequestStatusAsync(string pullRequestUrl)
        {
            string url = GetPrPartialAbsolutePath(pullRequestUrl);

            HttpResponseMessage response = await this.ExecuteGitCommand(HttpMethod.Get, url, _logger);

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            if (Enum.TryParse(responseContent["state"].ToString(), true, out PrStatus status))
            {
                if (status == PrStatus.Open)
                {
                    return status;
                }

                if (status == PrStatus.Closed)
                {
                    if (bool.TryParse(responseContent["merged"].ToString(), out bool merged))
                    {
                        if (merged)
                        {
                            return PrStatus.Merged;
                        }
                    }

                    return PrStatus.Closed;
                }
            }

            return PrStatus.None;
        }

        public async Task<string> GetPullRequestRepo(string pullRequestUrl)
        {
            string url = GetPrPartialAbsolutePath(pullRequestUrl);

            HttpResponseMessage response = await this.ExecuteGitCommand(HttpMethod.Get, url, _logger);

            JObject responseContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            return responseContent["base"]["repo"]["html_url"].ToString();
        }

        public async Task<string> CreatePullRequestAsync(string repoUri, string mergeWithBranch, string sourceBranch, string title = null, string description = null)
        {
            string linkToPullRquest = await CreateOrUpdatePullRequestAsync(repoUri, mergeWithBranch, sourceBranch, HttpMethod.Post, title, description);
            return linkToPullRquest;
        }

        public async Task<string> UpdatePullRequestAsync(string pullRequestUri, string mergeWithBranch, string sourceBranch, string title = null, string description = null)
        {
            string linkToPullRquest = await CreateOrUpdatePullRequestAsync(pullRequestUri, mergeWithBranch, sourceBranch, new HttpMethod("PATCH"), title, description);
            return linkToPullRquest;
        }

        public async Task MergePullRequestAsync(string pullRequestUrl, MergePullRequestParameters parameters)
        {
            var (owner, repo, id) = ParsePullRequestUri(pullRequestUrl);

            var mergePullRequest = new MergePullRequest
            {
                Sha = parameters.CommitToMerge,
                MergeMethod = parameters.SquashMerge ? PullRequestMergeMethod.Squash : PullRequestMergeMethod.Merge,
            };

            var pr = await Client.PullRequest.Get(owner, repo, id);
            await Client.PullRequest.Merge(owner, repo, id, mergePullRequest);

            if (parameters.DeleteSourceBranch)
            {
                await Client.Git.Reference.Delete(owner, repo, $"heads/{pr.Head.Ref}");
            }
        }

        public async Task<string> CreatePullRequestCommentAsync(string pullRequestUrl, string message)
        {
            var (owner, repo, id) = ParsePullRequestUri(pullRequestUrl);

            IssueComment comment = await Client.Issue.Comment.Create(owner, repo, id, message);

            return comment.Id.ToString();
        }

        public async Task UpdatePullRequestCommentAsync(string pullRequestUrl, string commentId, string message)
        {
            var (owner, repo, id) = ParsePullRequestUri(pullRequestUrl);
            if (!int.TryParse(commentId, out int commentIdValue))
            {
                throw new ArgumentException("The comment id '{commentId}' is in an invalid format", nameof(commentId));
            }

            await Client.Issue.Comment.Update(owner, repo, commentIdValue, message);
        }

        public async Task CommentOnPullRequestAsync(string pullRequestUrl, string message)
        {
            GitHubComment comment = new GitHubComment(message);

            string body = JsonConvert.SerializeObject(comment, _serializerSettings);

            var (owner, repo, id) = ParsePullRequestUri(pullRequestUrl);

            await this.ExecuteGitCommand(HttpMethod.Post, $"repos/{owner}/{repo}/issues/{id}/comments", _logger, body);
        }

        public async Task<List<GitFile>> GetFilesForCommitAsync(string repoUri, string commit, string path)
        {
            path = path.Replace('\\', '/');
            path = path.TrimStart('/').TrimEnd('/');

            var (owner, repo) = ParseRepoUri(repoUri);

            var pathTree = await GetTreeForPathAsync(owner, repo, commit, path);

            var recursiveTree = await GetRecursiveTreeAsync(owner, repo, pathTree.Sha);

            var files = await Task.WhenAll(
                recursiveTree.Tree
                    .Where(treeItem => treeItem.Type == TreeType.Blob)
                    .Select(
                        async treeItem =>
                        {
                            var blob = await Client.Git.Blob.Get(owner, repo, treeItem.Sha);
                            return new GitFile(
                                path + "/" + treeItem.Path,
                                blob.Content,
                                blob.Encoding == EncodingType.Base64 ? "base64" : "utf-8")
                            {
                                Mode = treeItem.Mode,
                            };
                        }));
            return files.ToList();
        }

        private async Task<TreeResponse> GetRecursiveTreeAsync(string owner, string repo, string treeSha)
        {
            var tree = await Client.Git.Tree.GetRecursive(owner, repo, treeSha);
            if (tree.Truncated)
            {
                throw new NotSupportedException(
                    $"The git repository is too large for the github api. Getting recursive tree '{treeSha}' returned truncated results.");
            }

            return tree;
        }

        private async Task<TreeResponse> GetTreeForPathAsync(string owner, string repo, string commitSha, string path)
        {
            var pathSegments = new Queue<string>(path.Split('/', '\\'));
            var currentPath = new List<string>();
            var commit = await Client.Git.Commit.Get(owner, repo, commitSha);

            var treeSha = commit.Tree.Sha;

            while (true)
            {
                var tree = await Client.Git.Tree.Get(owner, repo, treeSha);
                if (tree.Truncated)
                {
                    throw new NotSupportedException(
                        $"The git repository is too large for the github api. Getting tree '{treeSha}' returned truncated results.");
                }

                if (pathSegments.Count < 1)
                {
                    return tree;
                }

                var subfolder = pathSegments.Dequeue();
                currentPath.Add(subfolder);
                var subfolderItem = tree.Tree.Where(ti => ti.Type == TreeType.Tree)
                    .FirstOrDefault(ti => ti.Path == subfolder);
                if (subfolderItem == null)
                {
                    throw new DirectoryNotFoundException(
                        $"The path '{string.Join("/", currentPath)}' could not be found.");
                }

                treeSha = subfolderItem.Sha;
            }
        }

        public async Task GetCommitMapForPathAsync(string repoUri, string branch, string assetsProducedInCommit, List<GitFile> files, string pullRequestBaseBranch, string path = "eng/common/")
        {
            if (path.EndsWith("/"))
            {
                path = path.Substring(0, path.Length - 1);
            }
            _logger.LogInformation($"Getting the contents of file/files in '{path}' of repo '{repoUri}' at commit '{assetsProducedInCommit}'");

            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            HttpResponseMessage response = await this.ExecuteGitCommand(HttpMethod.Get, $"repos/{ownerAndRepo}/contents/{path}?ref={assetsProducedInCommit}", _logger);

            List<GitHubContent> contents = JsonConvert.DeserializeObject<List<GitHubContent>>(await response.Content.ReadAsStringAsync());

            foreach (GitHubContent content in contents)
            {
                if (content.Type == GitHubContentType.File)
                {
                    if (!GitFileManager.DependencyFiles.Contains(content.Path))
                    {
                        string fileContent = await GetFileContentsAsync(ownerAndRepo, content.Path);
                        GitFile gitCommit = new GitFile(content.Path, fileContent);
                        files.Add(gitCommit);
                    }
                }
                else
                {
                    await GetCommitMapForPathAsync(repoUri, branch, assetsProducedInCommit, files, pullRequestBaseBranch, content.Path);
                }
            }

            _logger.LogInformation($"Getting the contents of file/files in '{path}' of repo '{repoUri}' at commit '{assetsProducedInCommit}' succeeded!");
        }

        public async Task<string> GetFileContentsAsync(string ownerAndRepo, string path)
        {
            HttpResponseMessage response = await this.ExecuteGitCommand(HttpMethod.Get, $"repos/{ownerAndRepo}/contents/{path}", _logger);

            JObject file = JObject.Parse(await response.Content.ReadAsStringAsync());
            string encodedContent = file["content"].ToString();

            byte[] data = Convert.FromBase64String(encodedContent);
            string content = Encoding.UTF8.GetString(data);

            return content;
        }

        public HttpClient CreateHttpClient(string versionOverride = null)
        {
            HttpClient client = new HttpClient
            {
                BaseAddress = new Uri(GitHubApiUri)
            };
            client.DefaultRequestHeaders.Add("Authorization", $"Token {_personalAccessToken}");
            client.DefaultRequestHeaders.Add("User-Agent", _userAgent);

            return client;
        }

        public async Task<string> CheckIfFileExistsAsync(string repoUri, string filePath, string branch)
        {
            string commit;
            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            HttpResponseMessage response;

            try
            {
                response = await this.ExecuteGitCommand(HttpMethod.Get, $"repos/{ownerAndRepo}/contents/{filePath}?ref={branch}", _logger);
            }
            catch (HttpRequestException exc) when (exc.Message.Contains(((int)HttpStatusCode.NotFound).ToString()))
            {
                return null;
            }

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());
            commit = content["sha"].ToString();

            return commit;
        }

        public async Task<string> GetLastCommitShaAsync(string ownerAndRepo, string branch)
        {
            HttpResponseMessage response = await this.ExecuteGitCommand(HttpMethod.Get, $"repos/{ownerAndRepo}/commits/{branch}", _logger);

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());

            if (content == null)
            {
                throw new Exception($"No commits found in branch '{branch}' of repo '{ownerAndRepo}'!");
            }

            return content["sha"].ToString();
        }

        public async Task<IList<Check>> GetPullRequestChecksAsync(string pullRequestUrl)
        {
            string url = $"{GetPrPartialAbsolutePath(pullRequestUrl)}/commits";

            HttpResponseMessage response = await this.ExecuteGitCommand(HttpMethod.Get, url, _logger);
            JArray content = JArray.Parse(await response.Content.ReadAsStringAsync());
            JToken lastCommit = content.Last;
            string lastCommitSha = lastCommit["sha"].ToString();

            var (owner, repo, id) = ParsePullRequestUri(pullRequestUrl);
            response = await this.ExecuteGitCommand(
                HttpMethod.Get,
                $"repos/{owner}/{repo}/commits/{lastCommitSha}/status",
                _logger);

            var statusContent = JObject.Parse(await response.Content.ReadAsStringAsync());

            IList<Check> statuses = new List<Check>();
            foreach (JToken status in statusContent["statuses"])
            {
                if (Enum.TryParse(status["state"].ToString(), true, out CheckState state))
                {
                    statuses.Add(new Check(state, status["context"].ToString(), status["target_url"].ToString()));
                }
            }

            return statuses;
        }

        public async Task<string> GetPullRequestBaseBranch(string pullRequestUrl)
        {
            string url = GetPrPartialAbsolutePath(pullRequestUrl);

            HttpResponseMessage response = await this.ExecuteGitCommand(HttpMethod.Get, url, _logger);
            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());

            return content["head"]["ref"].ToString();
        }

        public async Task<IList<Commit>> GetPullRequestCommitsAsync(string pullRequestUrl)
        {
            var (owner, repo, id) = ParsePullRequestUri(pullRequestUrl);

            var commits = await Client.PullRequest.Commits(owner, repo, id);

            return commits.Select(c => new Commit(c.Author.Name, c.Sha)).ToList();
        }

        private static string GetOwnerAndRepo(string uri, Regex pattern)
        {
            var u = new UriBuilder(uri);
            var match = pattern.Match(u.Path);
            if (!match.Success)
            {
                return null;
            }

            return $"{match.Groups["owner"]}/{match.Groups["repo"]}";
        }

        private static Regex repoUriPattern = new Regex(@"^/(?<owner>[^/]+)/(?<repo>[^/]+)/?$");

        public static string GetOwnerAndRepoFromRepoUri(string repoUri)
        {
            return GetOwnerAndRepo(repoUri, repoUriPattern);
        }

        public static (string owner, string repo) ParseRepoUri(string uri)
        {
            var u = new UriBuilder(uri);
            var match = repoUriPattern.Match(u.Path);
            if (!match.Success)
            {
                return default;
            }

            return (match.Groups["owner"].Value, match.Groups["repo"].Value);
        }

        private static Regex prUriPattern = new Regex(@"^/repos/(?<owner>[^/]+)/(?<repo>[^/]+)/pulls/(?<id>\d+)$");

        public static (string owner, string repo, int id) ParsePullRequestUri(string uri)
        {
            var u = new UriBuilder(uri);
            var match = prUriPattern.Match(u.Path);
            if (!match.Success)
            {
                return default;
            }

            return (match.Groups["owner"].Value, match.Groups["repo"].Value, int.Parse(match.Groups["id"].Value));
        }

        private async Task<string> CreateOrUpdatePullRequestAsync(string uri, string mergeWithBranch, string sourceBranch, HttpMethod method, string title = null, string description = null)
        {
            string requestUri;

            title = !string.IsNullOrEmpty(title) ? $"{PullRequestProperties.TitleTag} {title}" : PullRequestProperties.Title;
            description = description ?? PullRequestProperties.Description;

            GitHubPullRequest pullRequest = new GitHubPullRequest(title, description, sourceBranch, mergeWithBranch);

            string body = JsonConvert.SerializeObject(pullRequest, _serializerSettings);

            if (method == HttpMethod.Post)
            {
                string ownerAndRepo = GetOwnerAndRepoFromRepoUri(uri);
                requestUri = $"repos/{ownerAndRepo}/pulls";
            }
            else
            {
                requestUri = GetPrPartialAbsolutePath(uri);
            }

            HttpResponseMessage response = await this.ExecuteGitCommand(method, requestUri, _logger, body);

            JObject content = JObject.Parse(await response.Content.ReadAsStringAsync());

            Console.WriteLine($"Browser ready link for this PR is: {content["html_url"].ToString()}");

            return content["url"].ToString();
        }

        private string GetPrPartialAbsolutePath(string prLink)
        {
            Uri uri = new Uri(prLink);
            return uri.PathAndQuery;
        }


        private async Task<TreeResponse> CreateGitHubTreeAsync(string owner, string repo, IEnumerable<GitFile> filesToCommit, TreeResponse baseTree)
        {
            var newTree = new NewTree
            {
                BaseTree = baseTree.Sha,
            };

            foreach (GitFile file in filesToCommit)
            {
                var newBlob = await Client.Git.Blob.Create(owner, repo, new NewBlob
                {
                    Content = file.Content,
                    Encoding = file.ContentEncoding == "base64" ? EncodingType.Base64 : EncodingType.Utf8,
                });
                newTree.Tree.Add(new NewTreeItem
                {
                    Path = file.FilePath,
                    Sha = newBlob.Sha,
                    Mode = file.Mode,
                    Type = TreeType.Blob,
                });
            }

            return await Client.Git.Tree.Create(owner, repo, newTree);
        }

        private async Task<string> PushCommitAsync(string ownerAndRepo, string commitMessage, string treeSha, string baseTreeSha)
        {
            GitHubCommit gitHubCommit = new GitHubCommit
            {
                Message = commitMessage,
                Tree = treeSha,
                Parents = new List<string> { baseTreeSha }
            };

            string body = JsonConvert.SerializeObject(gitHubCommit, _serializerSettings);
            HttpResponseMessage response = await this.ExecuteGitCommand(HttpMethod.Post, $"repos/{ownerAndRepo}/git/commits", _logger, body);
            JToken parsedResponse = JToken.Parse(response.Content.ReadAsStringAsync().Result);
            return parsedResponse["sha"].ToString();
        }

        private async Task<List<GitHubTreeItem>> GetTreeItems(string repoUri, string commit)
        {
            string ownerAndRepo = GetOwnerAndRepoFromRepoUri(repoUri);
            HttpResponseMessage response = await this.ExecuteGitCommand(HttpMethod.Get, $"repos/{ownerAndRepo}/commits/{commit}", _logger);
            JToken parsedResponse = JToken.Parse(response.Content.ReadAsStringAsync().Result);
            Uri treeUrl = new Uri(parsedResponse["commit"]["tree"]["url"].ToString());

            response = await this.ExecuteGitCommand(HttpMethod.Get, $"{treeUrl.PathAndQuery}?recursive=1", _logger);
            parsedResponse = JToken.Parse(response.Content.ReadAsStringAsync().Result);

            JArray tree = JArray.Parse(parsedResponse["tree"].ToString());

            List<GitHubTreeItem> treeItems = new List<GitHubTreeItem>();

            foreach (JToken item in tree)
            {
                GitHubTreeItem treeItem = new GitHubTreeItem
                {
                    Mode = item["mode"].ToString(),
                    Path = item["path"].ToString(),
                    Type = item["type"].ToString()
                };

                treeItems.Add(treeItem);
            }

            return treeItems;
        }
    }
}
