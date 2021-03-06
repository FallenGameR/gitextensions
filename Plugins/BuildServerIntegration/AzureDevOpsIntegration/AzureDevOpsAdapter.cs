using System;
using System.ComponentModel.Composition;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureDevOpsIntegration.Settings;
using GitUIPluginInterfaces;
using GitUIPluginInterfaces.BuildServerIntegration;

namespace AzureDevOpsIntegration
{
    [MetadataAttribute]
    [AttributeUsage(AttributeTargets.Class)]
    public class AzureDevOpsIntegrationMetadata : BuildServerAdapterMetadataAttribute
    {
        public AzureDevOpsIntegrationMetadata(string buildServerType)
            : base(buildServerType)
        {
        }
    }

    /// <summary>
    /// Provides build server integration for Azure DevOps (or TFS>=2015) into GitExtensions
    /// </summary>
    [Export(typeof(IBuildServerAdapter))]
    [AzureDevOpsIntegrationMetadata(PluginName)]
    [PartCreationPolicy(CreationPolicy.NonShared)]
    internal class AzureDevOpsAdapter : IBuildServerAdapter
    {
        public const string PluginName = "Azure DevOps and Team Foundation Server (since TFS2015)";

        private bool _firstCallForFinishedBuildsWasIgnored = false;
        private IBuildServerWatcher _buildServerWatcher;
        private IntegrationSettings _settings;
        private ApiClient _apiClient;
        private static readonly IBuildDurationFormatter _buildDurationFormatter = new BuildDurationFormatter();
        private Task<string> _buildDefinitionsTask;
        private string _projectUrl;
        private string _buildDefinitions;

        // Static variable used to convey the data between the different instances of the class but that doesn't necessarily require synchronisation because:
        // * there is only one instance at every given time (link to the revision grid and recreated on revision grid refresh)
        // * data is created by the first instance and used in readonly by the later instances
        private static CacheAzureDevOps CacheAzureDevOps = null;
        private string CacheKey => _projectUrl + "|" + _settings.BuildDefinitionFilter;

        public void Initialize(IBuildServerWatcher buildServerWatcher, ISettingsSource config, Func<ObjectId, bool> isCommitInRevisionGrid = null)
        {
            if (_buildServerWatcher != null)
            {
                throw new InvalidOperationException("Already initialized");
            }

            _buildServerWatcher = buildServerWatcher;
            _settings = IntegrationSettings.ReadFrom(config);

            if (!_settings.IsValid())
            {
                return;
            }

            _projectUrl = _buildServerWatcher.ReplaceVariables(_settings.ProjectUrl);

            if (!Uri.IsWellFormedUriString(_projectUrl, UriKind.Absolute) || string.IsNullOrWhiteSpace(_settings.ApiToken))
            {
                return;
            }

            _apiClient = new ApiClient(_projectUrl, _settings.ApiToken);
            if (CacheAzureDevOps == null || CacheAzureDevOps.Id != CacheKey)
            {
                CacheAzureDevOps = null;
                _buildDefinitionsTask = _apiClient.GetBuildDefinitionsAsync(_settings.BuildDefinitionFilter);
            }
            else
            {
                _buildDefinitions = CacheAzureDevOps.BuildDefinitions;
            }
        }

        /// <summary>
        /// Gets a unique key which identifies this build server.
        /// </summary>
        public string UniqueKey => _settings?.ProjectUrl ?? throw new InvalidOperationException($"{nameof(AzureDevOpsAdapter)} is not yet initialized");

        public IObservable<BuildInfo> GetFinishedBuildsSince(IScheduler scheduler, DateTime? sinceDate = null)
        {
            if (!_firstCallForFinishedBuildsWasIgnored)
            {
                _firstCallForFinishedBuildsWasIgnored = true;
                return Observable.Empty<BuildInfo>();
            }

            return GetBuilds(scheduler, sinceDate, false);
        }

        public IObservable<BuildInfo> GetRunningBuilds(IScheduler scheduler)
        {
            return GetBuilds(scheduler, null, true);
        }

        private IObservable<BuildInfo> GetBuilds(IScheduler scheduler, DateTime? sinceDate = null, bool? running = null)
        {
            return Observable.Create<BuildInfo>((observer, cancellationToken) => ObserveBuildsAsync(sinceDate, running, observer, cancellationToken));
        }

        private async Task ObserveBuildsAsync(DateTime? sinceDate, bool? running, IObserver<BuildInfo> observer, CancellationToken cancellationToken)
        {
            if (_apiClient == null)
            {
                observer.OnCompleted();
                return;
            }

            try
            {
                if (_buildDefinitions == null)
                {
                    _buildDefinitions = await _buildDefinitionsTask;

                    if (_buildDefinitions == null)
                    {
                        observer.OnCompleted();
                        return;
                    }

                    CacheAzureDevOps = new CacheAzureDevOps { Id = CacheKey, BuildDefinitions = _buildDefinitions };
                }

                var builds = await _apiClient.QueryBuildsAsync(_buildDefinitions, sinceDate, running);

                foreach (var build in builds)
                {
                    observer.OnNext(CreateBuildInfo(build));
                }
            }
            catch (OperationCanceledException)
            {
                // Do nothing, the observer is already stopped
            }
            catch (Exception ex)
            {
                observer.OnError(ex);
            }
        }

        private static BuildInfo CreateBuildInfo(Build buildDetail)
        {
            string duration = string.Empty;

            if (buildDetail.Status != null
                && buildDetail.Status != "none"
                && buildDetail.Status != "notStarted"
                && buildDetail.Status != "postponed"
                && buildDetail.StartTime.HasValue)
            {
                if (buildDetail.Status == "inProgress")
                {
                    duration = _buildDurationFormatter.Format((long)(DateTime.UtcNow - buildDetail.StartTime.Value).TotalMilliseconds);
                }
                else
                {
                    duration = buildDetail.FinishTime.HasValue ? _buildDurationFormatter.Format((long)(buildDetail.FinishTime.Value - buildDetail.StartTime.Value).TotalMilliseconds) : "???";
                }
            }

            var buildInfo = new BuildInfo
            {
                Id = buildDetail.BuildNumber,
                StartDate = buildDetail.StartTime ?? DateTime.Now.AddHours(1),
                Status = buildDetail.IsInProgress ? BuildInfo.BuildStatus.InProgress : MapResult(buildDetail.Result),
                Description = duration + " " + buildDetail.BuildNumber,
                Tooltip = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(buildDetail.IsInProgress ? buildDetail.Status : buildDetail.Result) + Environment.NewLine + duration + Environment.NewLine + buildDetail.BuildNumber,
                CommitHashList = new[] { ObjectId.Parse(buildDetail.SourceVersion) },
                Url = buildDetail._links.Web.Href,
                ShowInBuildReportTab = false
            };

            return buildInfo;
        }

        private static BuildInfo.BuildStatus MapResult(string status)
        {
            switch (status)
            {
                case "failed":
                    return BuildInfo.BuildStatus.Failure;
                case "canceled":
                    return BuildInfo.BuildStatus.Stopped;
                case "succeeded":
                    return BuildInfo.BuildStatus.Success;
                case "partiallySucceeded":
                    return BuildInfo.BuildStatus.Unstable;
                default:
                    return BuildInfo.BuildStatus.Unknown;
            }
        }

        public void Dispose()
        {
            _apiClient?.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
