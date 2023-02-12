﻿using AnyMapper;
using AnySerializer;
using Binner.Common.Extensions;
using Binner.Model.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TypeSupport.Extensions;
using static Binner.Model.Common.SystemDefaults;

namespace Binner.Common.StorageProviders
{
    public class BinnerFileStorageProvider : IStorageProvider
    {
        public const string ProviderName = "Binner";
        private const SerializerOptions SerializationOptions = SerializerOptions.EmbedTypes;

        private bool _isDisposed = false;
        private readonly SemaphoreSlim _dataLock = new(1, 1);
        private readonly BinnerFileStorageConfiguration _config;
        private readonly SerializerProvider _serializer = new();
        private readonly ManualResetEvent _quitEvent = new(false);
        private readonly Guid _instance = Guid.NewGuid();
        private PrimaryKeyTracker _primaryKeyTracker = new PrimaryKeyTracker(new Dictionary<string, long>());
        private IBinnerDb _db = new BinnerDbV3();
        private bool _isDirty;
        private Thread _ioThread = new Thread(new ThreadStart(() => { }));

        public BinnerDbVersion Version { get; private set; } = new BinnerDbVersion();

        public BinnerFileStorageProvider(IDictionary<string, string> config)
        {
            _config = new BinnerFileStorageConfiguration(config);
            ValidateBinnerConfiguration(_config);
            Task.Run(async () =>
            {
                await LoadDatabaseAsync();
            }).GetAwaiter().GetResult();
            StartIOThread();
        }


        private void ValidateBinnerConfiguration(BinnerFileStorageConfiguration config)
        {
            if (string.IsNullOrWhiteSpace(_config.Filename)) throw new BinnerConfigurationException($"The database filename specified in the configuration cannot be empty for the {nameof(BinnerFileStorageProvider)} storage provider.");
            if (OperatingSystem.IsWindows())
            {
            }
            else
            {
                if (_config.Filename.Contains(":\\"))
                {
                    throw new BinnerConfigurationException($"The database filename is invalid on this environment and should be a unix-compatible path: '{_config.Filename}'");
                }
            }
        }

        public async Task<IBinnerDb> GetDatabaseAsync(IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                return _db;
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public Task<ConnectionResponse> TestConnectionAsync()
        {
            return Task.FromResult(new ConnectionResponse { IsSuccess = true, DatabaseExists = File.Exists(_config.Filename), Errors = new List<string>() });
        }

        public async Task<OAuthCredential?> GetOAuthCredentialAsync(string providerName, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                return _db.OAuthCredentials
                    .Where(x => x.Provider == providerName && x.UserId == userContext?.UserId)
                    .FirstOrDefault();
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<OAuthCredential> SaveOAuthCredentialAsync(OAuthCredential credential, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                credential.UserId = userContext?.UserId;
                var existingCredential = _db.OAuthCredentials.Where(x => x.Provider == credential.Provider && x.UserId == credential.UserId).FirstOrDefault();
                if (existingCredential != null)
                {
                    existingCredential.AccessToken = credential.AccessToken;
                    existingCredential.RefreshToken = credential.RefreshToken;
                }
                else
                    _db.OAuthCredentials.Add(credential);
                _isDirty = true;
            }
            finally
            {
                _dataLock.Release();
            }
            return credential;
        }

        public async Task RemoveOAuthCredentialAsync(string providerName, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                var existingCredential = _db.OAuthCredentials.Where(x => x.Provider == providerName && x.UserId == userContext?.UserId).FirstOrDefault();
                if (existingCredential != null)
                {
                    _db.OAuthCredentials.Remove(existingCredential);
                    _isDirty = true;
                }
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<Part> AddPartAsync(Part part, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                part.UserId = userContext?.UserId;
                part.PartId = _primaryKeyTracker.GetNextKey<Part>();
                _db.Parts.Add(part);
                _isDirty = true;
            }
            finally
            {
                _dataLock.Release();
            }
            return part;
        }

        public async Task<Part> UpdatePartAsync(Part part, IUserContext userContext)
        {
            if (part == null) throw new ArgumentNullException(nameof(part));
            await _dataLock.WaitAsync();
            try
            {
                part.UserId = userContext?.UserId;
                var existingPart = GetPartInternal(part.PartId, userContext);
                if (existingPart != null)
                {
                    existingPart = Mapper.Map<Part, Part>(part, existingPart, x => x.PartId);
                    existingPart.PartId = part.PartId;
                    _isDirty = true;
                }
            }
            finally
            {
                _dataLock.Release();
            }
            return part;
        }

        public async Task<PartType?> GetOrCreatePartTypeAsync(PartType partType, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                partType.UserId = userContext?.UserId;
                var existingPartType = _db.PartTypes
                    .FirstOrDefault(x => x.Name?.Equals(partType.Name, StringComparison.InvariantCultureIgnoreCase) == true && x.UserId == partType.UserId);
                if (existingPartType == null)
                {
                    existingPartType = new PartType
                    {
                        Name = partType.Name,
                        ParentPartTypeId = partType.ParentPartTypeId,
                        PartTypeId = _primaryKeyTracker.GetNextKey<PartType>(),
                        UserId = partType.UserId,
                    };
                    _db.PartTypes.Add(existingPartType);
                    _isDirty = true;
                }
                return existingPartType;
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<ICollection<PartType>> GetPartTypesAsync(IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                return _db.PartTypes
                    .Where(x => x.UserId == userContext?.UserId)
                    .ToList();
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<PartType?> GetPartTypeAsync(long partTypeId, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                return GetPartTypeInternal(partTypeId, userContext);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        private PartType? GetPartTypeInternal(long partTypeId, IUserContext? userContext)
        {
            return _db.PartTypes
                .Where(x => x.PartTypeId == partTypeId && x.UserId == userContext?.UserId)
                .FirstOrDefault();
        }

        public async Task<PartType> UpdatePartTypeAsync(PartType partType, IUserContext userContext)
        {
            if (partType == null) throw new ArgumentNullException(nameof(partType));
            await _dataLock.WaitAsync();
            try
            {
                partType.UserId = userContext?.UserId;
                var existingPartType = GetPartTypeInternal(partType.PartTypeId, userContext);
                if (existingPartType != null)
                {
                    existingPartType = Mapper.Map<PartType, PartType>(partType, existingPartType, x => x.PartTypeId);
                    existingPartType.PartTypeId = partType.PartTypeId;
                    _isDirty = true;
                }
            }
            finally
            {
                _dataLock.Release();
            }
            return partType;
        }

        public async Task<bool> DeletePartAsync(Part part, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                part.UserId = userContext?.UserId;
                var itemRemoved = _db.Parts.Remove(part);
                if (itemRemoved)
                {
                    var nextPartId = _db.Parts.OrderByDescending(x => x.PartId)
                        .Select(x => x.PartId)
                        .FirstOrDefault() + 1;
                    _primaryKeyTracker.SetNextKey<Part>(nextPartId);
                    _isDirty = true;
                }
                return itemRemoved;
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<ICollection<SearchResult<Part>>> FindPartsAsync(string keywords, IUserContext userContext)
        {
            if (string.IsNullOrEmpty(keywords)) throw new ArgumentNullException(nameof(keywords));

            await _dataLock.WaitAsync();
            try
            {
                var comparisonType = StringComparison.InvariantCultureIgnoreCase;
                var words = keywords.Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries);
                var matches = new List<SearchResult<Part>>();

                // exact match first
                matches.AddRange(GetExactMatches(words, comparisonType, userContext).ToList());
                matches.AddRange(GetPartialMatches(words, comparisonType, userContext).ToList());

                return matches
                    .OrderBy(x => x.Rank)
                    .DistinctBy(x => x.Result)
                    .ToList();
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<Part?> GetPartAsync(long partId, IUserContext userContext)
        {
            if (partId <= 0) throw new ArgumentException(nameof(partId));
            await _dataLock.WaitAsync();
            try
            {
                return GetPartInternal(partId, userContext);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        private Part? GetPartInternal(long partId, IUserContext? userContext)
        {
            return _db.Parts.Where(x => x.PartId == partId && x.UserId == userContext?.UserId)
                .FirstOrDefault();
        }

        public async Task<Part?> GetPartAsync(string partNumber, IUserContext userContext)
        {
            if (string.IsNullOrEmpty(partNumber)) throw new ArgumentNullException(nameof(partNumber));
            await _dataLock.WaitAsync();
            try
            {
                return _db.Parts.FirstOrDefault(x => x.PartNumber?.Equals(partNumber, StringComparison.InvariantCultureIgnoreCase) == true && x.UserId == userContext?.UserId);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<long> GetPartsCountAsync(IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                return _db.Parts.Where(x => x.UserId == userContext?.UserId)
                    .Sum(x => x.Quantity);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<long> GetUniquePartsCountAsync(IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                return _db.Parts.Where(x => x.UserId == userContext?.UserId)
                    .Count();
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<decimal> GetPartsValueAsync(IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                return _db.Parts.Where(x => x.UserId == userContext?.UserId)
                    .Sum(x => x.Cost * x.Quantity);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<PaginatedResponse<Part>> GetLowStockAsync(PaginatedRequest request, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            var pageRecords = (request.Page - 1) * request.Results;
            try
            {
                var totalItems = _db.Parts.Where(x => x.Quantity <= x.LowStockThreshold && x.UserId == userContext?.UserId)
                    .Count();
                var parts = _db.Parts
                    .Where(x => x.Quantity <= x.LowStockThreshold && x.UserId == userContext?.UserId)
                    .OrderBy(string.IsNullOrEmpty(request.OrderBy) ? "PartId" : request.OrderBy, request.Direction)
                    .Skip(pageRecords)
                    .Take(request.Results)
                    .ToList();
                return new PaginatedResponse<Part>(totalItems, request.Results, request.Page, parts);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<PaginatedResponse<Part>> GetPartsAsync(PaginatedRequest request, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            var pageRecords = (request.Page - 1) * request.Results;
            try
            {
                var totalItems = _db.Parts
                    .Where(x => x.UserId == userContext?.UserId)
                    .WhereIf(!string.IsNullOrEmpty(request.By), x => x.GetPropertyValue(request.By!.UcFirst())?.ToString() == request.Value)
                    .Count();
                var parts = _db.Parts
                    .Where(x => x.UserId == userContext?.UserId)
                    .WhereIf(!string.IsNullOrEmpty(request.By), x => x.GetPropertyValue(request.By!.UcFirst())?.ToString() == request.Value)
                    .OrderBy(string.IsNullOrEmpty(request.OrderBy) ? "PartId" : request.OrderBy, request.Direction)
                    .Skip(pageRecords)
                    .Take(request.Results)
                    .ToList();
                return new PaginatedResponse<Part>(totalItems, request.Results, request.Page, parts);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<ICollection<Part>> GetPartsAsync(Expression<Func<Part, bool>> predicate, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                return _db.Parts.Where(predicate.Compile()).ToList();
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<Project?> GetProjectAsync(long projectId, IUserContext userContext)
        {
            if (projectId <= 0) throw new ArgumentException(nameof(projectId));
            await _dataLock.WaitAsync();
            try
            {
                return GetProjectInternal(projectId, userContext);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public Project? GetProjectInternal(long projectId, IUserContext? userContext)
        {
            return _db.Projects
                .FirstOrDefault(x => x.ProjectId.Equals(projectId) && x.UserId == userContext?.UserId);
        }

        public async Task<Project?> GetProjectAsync(string projectName, IUserContext userContext)
        {
            if (string.IsNullOrEmpty(projectName)) throw new ArgumentNullException(nameof(projectName));
            await _dataLock.WaitAsync();
            try
            {
                return _db.Projects
                    .FirstOrDefault(x => x.Name?.Equals(projectName, StringComparison.InvariantCultureIgnoreCase) == true && x.UserId == userContext?.UserId);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<ICollection<Project>> GetProjectsAsync(PaginatedRequest request, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            var pageRecords = (request.Page - 1) * request.Results;
            try
            {
                return _db.Projects
                    .Where(x => x.UserId == userContext?.UserId)
                    .OrderBy(string.IsNullOrEmpty(request.OrderBy) ? "ProjectId" : request.OrderBy, request.Direction)
                    .Skip(pageRecords)
                    .Take(request.Results)
                    .ToList();
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<Project> AddProjectAsync(Project project, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                project.UserId = userContext?.UserId;
                project.ProjectId = _primaryKeyTracker.GetNextKey<Project>();
                _db.Projects.Add(project);
                _isDirty = true;
            }
            finally
            {
                _dataLock.Release();
            }
            return project;
        }

        public async Task<Project> UpdateProjectAsync(Project project, IUserContext userContext)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            await _dataLock.WaitAsync();
            try
            {
                project.UserId = userContext?.UserId;
                var existingProject = GetProjectInternal(project.ProjectId, userContext);
                if (existingProject != null)
                {
                    existingProject = Mapper.Map<Project, Project>(project, existingProject, x => x.ProjectId);
                    existingProject.ProjectId = project.ProjectId;
                    _isDirty = true;
                }
            }
            finally
            {
                _dataLock.Release();
            }
            return project;
        }

        public async Task<bool> DeletePartTypeAsync(PartType partType, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                partType.UserId = userContext?.UserId;
                var itemRemoved = _db.PartTypes.Remove(partType);
                if (itemRemoved)
                {
                    var nextPartTypeId = _db.PartTypes.OrderByDescending(x => x.PartTypeId)
                        .Select(x => x.PartTypeId)
                        .FirstOrDefault() + 1;
                    _primaryKeyTracker.SetNextKey<PartType>(nextPartTypeId);
                    _isDirty = true;
                }
                return itemRemoved;
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<bool> DeleteProjectAsync(Project project, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                project.UserId = userContext?.UserId;
                var itemRemoved = _db.Projects.Remove(project);
                if (itemRemoved)
                {
                    var nextProjectId = _db.Projects.OrderByDescending(x => x.ProjectId)
                        .Select(x => x.ProjectId)
                        .FirstOrDefault();
                    nextProjectId++;
                    _primaryKeyTracker.SetNextKey<Project>(nextProjectId);
                    _isDirty = true;
                }
                return itemRemoved;
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<StoredFile> AddStoredFileAsync(StoredFile storedFile, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                storedFile.UserId = userContext?.UserId;
                storedFile.StoredFileId = _primaryKeyTracker.GetNextKey<StoredFile>();

                (_db as BinnerDbV3)?.StoredFiles.Add(storedFile);
                _isDirty = true;
            }
            finally
            {
                _dataLock.Release();
            }
            return storedFile;
        }

        public async Task<StoredFile?> GetStoredFileAsync(long storedFileId, IUserContext userContext)
        {
            if (storedFileId <= 0) throw new ArgumentException(nameof(storedFileId));
            await _dataLock.WaitAsync();
            try
            {
                return ((_db as BinnerDbV3)?.StoredFiles)?.FirstOrDefault(x => x.StoredFileId.Equals(storedFileId) && x.UserId == userContext?.UserId);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<StoredFile?> GetStoredFileAsync(string filename, IUserContext userContext)
        {
            if (string.IsNullOrEmpty(filename)) throw new ArgumentException(nameof(filename));
            await _dataLock.WaitAsync();
            try
            {
                return ((_db as BinnerDbV3)?.StoredFiles)?.FirstOrDefault(x => x.FileName.Equals(filename) && x.UserId == userContext?.UserId);
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<ICollection<StoredFile>> GetStoredFilesAsync(long partId, StoredFileType? fileType, IUserContext userContext)
        {
            if (partId <= 0) throw new ArgumentException(nameof(partId));
            await _dataLock.WaitAsync();
            try
            {
                return (_db as BinnerDbV3)?.StoredFiles
                    .Where(x => x.PartId.Equals(partId))
                    .Where(x => fileType == null || x.StoredFileType.Equals(fileType))
                    .Where(x => x.UserId == userContext?.UserId)
                    .ToList() ?? new List<StoredFile>();
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<ICollection<StoredFile>> GetStoredFilesAsync(PaginatedRequest request, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            var pageRecords = (request.Page - 1) * request.Results;
            try
            {
                return (_db as BinnerDbV3)?.StoredFiles
                    .Where(x => x.UserId == userContext?.UserId)
                    .OrderBy(string.IsNullOrEmpty(request.OrderBy) ? "StoredFileId" : request.OrderBy, request.Direction)
                    .Skip(pageRecords)
                    .Take(request.Results)
                    .ToList() ?? new List<StoredFile>();
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<bool> DeleteStoredFileAsync(StoredFile storedFile, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                storedFile.UserId = userContext?.UserId;
                var itemRemoved = (_db as BinnerDbV3)?.StoredFiles.Remove(storedFile);
                if (itemRemoved == true)
                {
                    var nextStoredFileId = ((_db as BinnerDbV3)?.StoredFiles.OrderByDescending(x => x.StoredFileId)
                        .Select(x => x.StoredFileId)
                        .FirstOrDefault() ?? 0);
                    nextStoredFileId++;
                    _primaryKeyTracker.SetNextKey<StoredFile>(nextStoredFileId);
                    _isDirty = true;
                }
                return itemRemoved == true;
            }
            finally
            {
                _dataLock.Release();
            }
        }

        public async Task<StoredFile> UpdateStoredFileAsync(StoredFile storedFile, IUserContext userContext)
        {
            if (storedFile == null) throw new ArgumentNullException(nameof(storedFile));
            await _dataLock.WaitAsync();
            try
            {
                storedFile.UserId = userContext?.UserId;
                var existingStoredFile = ((_db as BinnerDbV3)?.StoredFiles)
                    ?.FirstOrDefault(x => x.StoredFileId.Equals(storedFile.StoredFileId) && x.UserId == userContext?.UserId);
                if (existingStoredFile != null)
                {
                    existingStoredFile = Mapper.Map<StoredFile, StoredFile>(storedFile, existingStoredFile, x => x.StoredFileId);
                    existingStoredFile.StoredFileId = storedFile.StoredFileId;
                    _isDirty = true;
                }
            }
            finally
            {
                _dataLock.Release();
            }
            return storedFile;
        }

        public async Task<Model.Common.OAuthAuthorization> CreateOAuthRequestAsync(Model.Common.OAuthAuthorization authRequest, IUserContext userContext)
        {
            await _dataLock.WaitAsync();
            try
            {
                var oAuthRequest = new OAuthRequest
                {
                    UserId = userContext?.UserId,
                    OAuthRequestId = (int)_primaryKeyTracker.GetNextKey<OAuthRequest>(),
                    RequestId = authRequest.Id,
                    Provider = authRequest.Provider,
                    ReturnToUrl = authRequest.ReturnToUrl,
                    Error = authRequest.Error,
                    ErrorDescription = authRequest.ErrorDescription,
                    AuthorizationReceived = false
                };

                (_db as BinnerDbV3)?.OAuthRequests.Add(oAuthRequest);
                _isDirty = true;
            }
            finally
            {
                _dataLock.Release();
            }
            return authRequest;
        }

        public async Task<Model.Common.OAuthAuthorization> UpdateOAuthRequestAsync(Model.Common.OAuthAuthorization authRequest, IUserContext userContext)
        {
            if (authRequest == null) throw new ArgumentNullException(nameof(authRequest));
            await _dataLock.WaitAsync();
            try
            {
                var oAuthRequest = new OAuthRequest
                {
                    AuthorizationCode = authRequest.AuthorizationCode,
                    AuthorizationReceived = authRequest.AuthorizationReceived,
                    Error = authRequest.Error,
                    ErrorDescription = authRequest.ErrorDescription,
                    Provider = authRequest.Provider,
                    RequestId = authRequest.Id,
                    ReturnToUrl = authRequest.ReturnToUrl,
                    UserId = userContext?.UserId
                };

                var existingOAuthRequest = ((_db as BinnerDbV3)?.OAuthRequests)
                    ?.FirstOrDefault(x => x.UserId == userContext?.UserId && x.Provider == oAuthRequest.Provider && x.RequestId == oAuthRequest.RequestId);
                if (existingOAuthRequest != null)
                {
                    existingOAuthRequest =
                        Mapper.Map<OAuthRequest, OAuthRequest>(oAuthRequest, existingOAuthRequest,
                            x => x.OAuthRequestId);
                    existingOAuthRequest.UserId = userContext?.UserId;
                    existingOAuthRequest.DateCreatedUtc = existingOAuthRequest.DateCreatedUtc;
                    existingOAuthRequest.DateModifiedUtc = DateTime.UtcNow;
                    _isDirty = true;
                }
            }
            finally
            {
                _dataLock.Release();
            }
            return authRequest;
        }

        public async Task<Model.Common.OAuthAuthorization?> GetOAuthRequestAsync(Guid requestId, IUserContext userContext)
        {
            if (requestId == Guid.Empty) throw new ArgumentException(nameof(requestId));
            await _dataLock.WaitAsync();
            try
            {
                var existingOAuthRequest = ((_db as BinnerDbV3)?.OAuthRequests)
                    ?.FirstOrDefault(x => x.RequestId.Equals(requestId) && x.UserId == userContext?.UserId);
                if (existingOAuthRequest == null)
                    return null;
                return new Model.Common.OAuthAuthorization(existingOAuthRequest.Provider, existingOAuthRequest.RequestId)
                {
                    AuthorizationCode = existingOAuthRequest.AuthorizationCode,
                    AuthorizationReceived = existingOAuthRequest.AuthorizationReceived,
                    Error = existingOAuthRequest.Error ?? string.Empty,
                    ErrorDescription = existingOAuthRequest.ErrorDescription ?? string.Empty,
                    Provider = existingOAuthRequest.Provider,
                    ReturnToUrl = existingOAuthRequest.ReturnToUrl ?? string.Empty,
                    UserId = userContext?.UserId,
                    CreatedUtc = existingOAuthRequest.DateCreatedUtc,
                };
            }
            finally
            {
                _dataLock.Release();
            }
        }

        private ICollection<SearchResult<Part>> GetExactMatches(ICollection<string> words, StringComparison comparisonType, IUserContext userContext)
        {
            var matches = new List<SearchResult<Part>>();

            // by part number
            var partNumberMatches = _db.Parts.Where(x => words.Any(y => x.PartNumber?.Equals(y, comparisonType) == true) && x.UserId == userContext?.UserId)
                .Select(x => new SearchResult<Part>(x, 10))
                .ToList();
            matches.AddRange(partNumberMatches);

            // by keyword
            var keywordMatches = _db.Parts
                .Where(x => words.Any(y => x.Keywords?.Contains(y, StringComparer.OrdinalIgnoreCase) == true) && x.UserId == userContext?.UserId)
                .Select(x => new SearchResult<Part>(x, 20))
                .ToList();
            matches.AddRange(keywordMatches);

            // by description
            var descriptionMatches = _db.Parts.Where(x => words.Any(y => x.Description?.Equals(y, comparisonType) == true) && x.UserId == userContext?.UserId)
                .Select(x => new SearchResult<Part>(x, 30))
                .ToList();
            matches.AddRange(descriptionMatches);

            // by digikey id
            var digikeyMatches = _db.Parts.Where(x => words.Any(y => x.DigiKeyPartNumber?.Equals(y, comparisonType) == true) && x.UserId == userContext?.UserId)
                .Select(x => new SearchResult<Part>(x, 40))
                .ToList();
            matches.AddRange(digikeyMatches);

            // by bin number
            var binNumberMatches = _db.Parts.Where(x => words.Any(y => x.BinNumber?.Equals(y, comparisonType) == true || x.BinNumber2?.Equals(y, comparisonType) == true) && x.UserId == userContext?.UserId)
                .Select(x => new SearchResult<Part>(x, 50))
                .ToList();
            matches.AddRange(binNumberMatches);

            return matches;
        }

        private ICollection<SearchResult<Part>> GetPartialMatches(ICollection<string> words, StringComparison comparisonType, IUserContext userContext)
        {
            var matches = new List<SearchResult<Part>>();

            // by part number
            var partNumberMatches = _db.Parts.Where(x => words.Any(y => x.PartNumber?.Contains(y, comparisonType) == true) && x.UserId == userContext?.UserId)
                .Select(x => new SearchResult<Part>(x, 100))
                .ToList();
            matches.AddRange(partNumberMatches);

            // by keyword
            var keywordMatches = _db.Parts
                .Where(x => words.Any(y => x.Keywords?.Contains(y, StringComparer.OrdinalIgnoreCase) == true) && x.UserId == userContext?.UserId)
                .Select(x => new SearchResult<Part>(x, 200))
                .ToList();
            matches.AddRange(keywordMatches);

            // by description
            var descriptionMatches = _db.Parts.Where(x => words.Any(y => x.Description?.Contains(y, comparisonType) == true) && x.UserId == userContext?.UserId)
                .Select(x => new SearchResult<Part>(x, 300))
                .ToList();
            matches.AddRange(descriptionMatches);

            // by digikey id
            var digikeyMatches = _db.Parts.Where(x => words.Any(y => x.DigiKeyPartNumber?.Contains(y, comparisonType) == true) && x.UserId == userContext?.UserId)
                .Select(x => new SearchResult<Part>(x, 400))
                .ToList();
            matches.AddRange(digikeyMatches);

            // by bin number
            var binNumberMatches = _db.Parts.Where(x => words.Any(y => x.BinNumber?.Contains(y, comparisonType) == true || x.BinNumber2?.Contains(y, comparisonType) == true) && x.UserId == userContext?.UserId)
                .Select(x => new SearchResult<Part>(x, 500))
                .ToList();
            matches.AddRange(binNumberMatches);

            return matches;
        }

        /// <summary>
        /// Load the database from disk
        /// </summary>
        /// <param name="requireLock">True if a lock needs to be acquired before saving</param>
        /// <returns></returns>
        private async Task LoadDatabaseAsync(bool requireLock = true)
        {
            if (File.Exists(_config.Filename))
            {
                if (requireLock)
                    await _dataLock.WaitAsync();
                try
                {
                    using (var stream = File.OpenRead(_config.Filename))
                    {
                        Version = ReadDbVersion(stream);

                        // copy the rest of the bytes
                        var bytes = new byte[stream.Length - stream.Position];
                        stream.Read(bytes, 0, bytes.Length);

                        _db = LoadDatabaseByVersion(Version, bytes);
                        bytes = null;

                        // could make this non-fatal
                        if (!ValidateChecksum(_db))
                            throw new Exception("The database did not pass validation! It is either corrupt or has been modified outside of the application.");
                        _primaryKeyTracker = new PrimaryKeyTracker(new Dictionary<string, long>
                        {
                            // there is no guaranteed order so we must sort first
                            { typeof(Part).Name, Math.Max(_db.Parts.OrderByDescending(x => x.PartId).Select(x => x.PartId).FirstOrDefault() + 1, 1) },
                            { typeof(PartType).Name, Math.Max(_db.PartTypes.OrderByDescending(x => x.PartTypeId).Select(x => x.PartTypeId).FirstOrDefault() + 1, 1) },
                            { typeof(Project).Name, Math.Max(_db.Projects.OrderByDescending(x => x.ProjectId).Select(x => x.ProjectId).FirstOrDefault() + 1, 1) },
                            { typeof(StoredFile).Name, Math.Max((_db as BinnerDbV3)?.StoredFiles.OrderByDescending(x => x.StoredFileId).Select(x => x.StoredFileId).FirstOrDefault() ?? 0 + 1, 1) },
                            { typeof(OAuthRequest).Name, Math.Max((_db as BinnerDbV3)?.OAuthRequests.OrderByDescending(x => x.OAuthRequestId).Select(x => x.OAuthRequestId).FirstOrDefault() ?? 0 + 1, 1) },
                        });
                    }
                }
                catch (Exception ex)
                {
                    throw new Exception("Failed to load database!", ex);
                }
                finally
                {
                    if (requireLock)
                        _dataLock.Release();
                }
            }
            else
            {
                if (requireLock)
                    await _dataLock.WaitAsync();
                try
                {
                    _db = NewDatabase();
                }
                finally
                {
                    if (requireLock)
                        _dataLock.Release();
                }
            }
        }

        private IBinnerDb LoadDatabaseByVersion(BinnerDbVersion version, byte[] bytes)
        {
            IBinnerDb db;
            // Support database loading by version number
            try
            {
                db = version.Version switch
                {
                    // Version 1
                    BinnerDbV1.VersionNumber => new BinnerDbV3(_serializer.Deserialize<BinnerDbV1>(bytes, SerializationOptions), (upgradeDb) => BuildChecksum(upgradeDb)),
                    // Version 2
                    BinnerDbV2.VersionNumber => new BinnerDbV3(_serializer.Deserialize<BinnerDbV2>(bytes, SerializationOptions), (upgradeDb) => BuildChecksum(upgradeDb)),
                    // Version 3
                    BinnerDbV3.VersionNumber => _serializer.Deserialize<BinnerDbV3>(bytes, SerializationOptions),
                    _ => throw new InvalidOperationException($"Unsupported database version: {version}"),
                };
                return db;
            }
            catch (Exception ex)
            {
                throw new BinnerConfigurationException($"Failed to load Binner file database!", ex);
            }
        }

        /// <summary>
        /// Save the database to disk
        /// </summary>
        /// <param name="requireLock">True if a lock needs to be acquired before saving</param>
        /// <returns></returns>
        private async Task SaveDatabaseAsync(bool requireLock = true)
        {
            if (requireLock)
                await _dataLock.WaitAsync();
            try
            {
                var db = _db as BinnerDbV3;
                if (db == null) return;

                db.FirstPartId = db.Parts
                    .OrderBy(x => x.PartId)
                    .Select(x => x.PartId)
                    .FirstOrDefault();
                db.LastPartId = db.Parts
                    .OrderByDescending(x => x.PartId)
                    .Select(x => x.PartId)
                    .FirstOrDefault();
                db.Count = db.Parts.Count;
                db.Checksum = BuildChecksum(db);
                using var stream = new MemoryStream();
                WriteDbVersion(stream, new BinnerDbVersion(BinnerDbV3.VersionNumber, BinnerDbV3.VersionCreated));
                var serializedBytes = _serializer.Serialize(db, SerializationOptions);
                stream.Write(serializedBytes, 0, serializedBytes.Length);
                var directoryName = Path.GetDirectoryName(_config.Filename);
                if (!string.IsNullOrEmpty(directoryName))
                    Directory.CreateDirectory(directoryName);
                File.WriteAllBytes(_config.Filename, stream.ToArray());
            }
            catch (Exception ex)
            {
                throw new Exception("There was an error trying to save the database!", ex);
            }
            finally
            {
                if (requireLock)
                    _dataLock.Release();
            }
        }

        private BinnerDbVersion ReadDbVersion(Stream stream)
        {
            using var reader = new BinaryReader(stream, Encoding.UTF8, true);
            var versionByte = reader.ReadByte();
            var versionCreated = reader.ReadInt64();
            return new BinnerDbVersion(versionByte, DateTime.FromBinary(versionCreated));
        }

        private void WriteDbVersion(Stream stream, BinnerDbVersion version)
        {
            using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
            writer.Write(version.Version);
            writer.Write(version.Created.ToBinary());
        }

        private IBinnerDb NewDatabase()
        {
            var created = DateTime.UtcNow;
            _primaryKeyTracker = new PrimaryKeyTracker(new Dictionary<string, long>
            {
                { typeof(Part).Name, 1 },
                { typeof(PartType).Name, 1 },
                { typeof(Project).Name, 1 },
                { typeof(StoredFile).Name, 1 },
            });

            // seed data
            var initialPartTypes = new List<PartType>();
            var defaultPartTypes = typeof(SystemDefaults.DefaultPartTypes).GetExtendedType();
            foreach (var partType in defaultPartTypes.EnumValues)
            {
                int? parentPartTypeId = null;
                var partTypeEnum = (DefaultPartTypes)partType.Key;
                var field = typeof(DefaultPartTypes).GetField(partType.Value);
                if (field?.IsDefined(typeof(ParentPartTypeAttribute), false) == true)
                {
                    var customAttribute = Attribute.GetCustomAttribute(field, typeof(ParentPartTypeAttribute)) as ParentPartTypeAttribute;
                    if (customAttribute != null)
                        parentPartTypeId = (int)customAttribute.Parent;
                }
                initialPartTypes.Add(new PartType
                {
                    PartTypeId = (int)partType.Key,
                    ParentPartTypeId = parentPartTypeId,
                    Name = partType.Value,
                    DateCreatedUtc = created,
                });
            }

            return new BinnerDbV3
            {
                Count = 0,
                FirstPartId = 0,
                LastPartId = 0,
                DateCreatedUtc = created,
                DateModifiedUtc = created,
                PartTypes = initialPartTypes,
                Parts = new List<Part>(),
                Projects = new List<Project>(),
                OAuthCredentials = new List<OAuthCredential>(),
                StoredFiles = new List<StoredFile>(),
                OAuthRequests = new List<OAuthRequest>(),
            };
        }

        private string BuildChecksum(IBinnerDb db)
        {
            var bytes = _serializer.Serialize(db, "Checksum");
            using var sha1 = SHA1.Create();
            var hash = Convert.ToBase64String(sha1.ComputeHash(bytes));
            return hash;
        }

        private bool ValidateChecksum(IBinnerDb db)
        {
            var calculatedChecksum = BuildChecksum(db);
            if (db.Checksum?.Equals(calculatedChecksum) == true)
                return true;
            return false;
        }

        private void StartIOThread()
        {
            _ioThread = new Thread(new ThreadStart(SaveDataThread));
            _ioThread.Start();
        }

        private void SaveDataThread()
        {
            while (!_quitEvent.WaitOne(500))
            {
                if (_isDirty)
                {
                    _dataLock.Wait();
                    try
                    {
                        Task.Run(async () => await SaveDatabaseAsync(false))
                            .GetAwaiter()
                            .GetResult();
                    }
                    finally
                    {
                        _isDirty = false;
                        _dataLock.Release();
                    }
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool isDisposing)
        {
            if (_isDisposed)
                return;
            _isDisposed = true;
            if (isDisposing)
            {
                // obtain the lock before removing it
                _dataLock.Wait();
                try
                {
                    _quitEvent.Set();

                    if (_isDirty)
                    {
                        Task.Run(async () =>
                        {
                            await SaveDatabaseAsync(false);
                        }).GetAwaiter().GetResult();
                    }

                    _quitEvent.Dispose();
                }
                finally
                {
                    _dataLock.Release();
                    try
                    {
                        _dataLock?.Dispose();
                    }
                    catch (Exception) { }
                }
            }
        }
    }
}
