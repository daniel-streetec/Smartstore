﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Smartstore.Admin.Models.Maintenance;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Checkout.Shipping;
using Smartstore.Core.Common.Services;
using Smartstore.Core.Common.Settings;
using Smartstore.Core.Content.Media.Imaging;
using Smartstore.Core.Data;
using Smartstore.Core.Security;
using Smartstore.Data;
using Smartstore.Data.Caching;
using Smartstore.Http;
using Smartstore.IO;
using Smartstore.Scheduling;
using Smartstore.Utilities;
using Smartstore.Web.Controllers;
using Smartstore.Web.Models.DataGrid;

namespace Smartstore.Admin.Controllers
{
    public class MaintenanceController : AdminController
    {
        private const string BACKUP_DIR = "Backups";

        private readonly SmartDbContext _db;
        private readonly IMemoryCache _memCache;
        private readonly ITaskScheduler _taskScheduler;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly Lazy<IImageCache> _imageCache;
        private readonly Lazy<IFilePermissionChecker> _filePermissionChecker;
        private readonly Lazy<ICurrencyService> _currencyService;
        private readonly Lazy<IPaymentService> _paymentService;
        private readonly Lazy<IShippingService> _shippingService;
        private readonly Lazy<CurrencySettings> _currencySettings;
        private readonly Lazy<MeasureSettings> _measureSettings;

        public MaintenanceController(
            SmartDbContext db,
            IMemoryCache memCache,
            ITaskScheduler taskScheduler,
            IHttpClientFactory httpClientFactory,
            Lazy<IImageCache> imageCache,
            Lazy<IFilePermissionChecker> filePermissionChecker,
            Lazy<ICurrencyService> currencyService,
            Lazy<IPaymentService> paymentService,
            Lazy<IShippingService> shippingService,
            Lazy<CurrencySettings> currencySettings,
            Lazy<MeasureSettings> measureSettings)
        {
            _db = db;
            _memCache = memCache;
            _taskScheduler = taskScheduler;
            _httpClientFactory = httpClientFactory;
            _imageCache = imageCache;
            _filePermissionChecker = filePermissionChecker;
            _currencyService = currencyService;
            _paymentService = paymentService;
            _shippingService = shippingService;
            _currencySettings = currencySettings;
            _measureSettings = measureSettings;
        }

        [Permission(Permissions.System.Maintenance.Read)]
        public async Task<IActionResult> Index()
        {
            var model = new MaintenanceModel();
            model.DeleteGuests.EndDate = DateTime.UtcNow.AddDays(-7);
            model.DeleteGuests.OnlyWithoutShoppingCart = true;

            // Image cache stats
            var (fileCount, totalSize) = await _imageCache.Value.CacheStatisticsAsync();
            model.DeleteImageCache.NumFiles = fileCount;
            model.DeleteImageCache.TotalSize = Prettifier.HumanizeBytes(totalSize);

            return View(model);
        }

        [Permission(Permissions.System.Maintenance.Execute)]
        public IActionResult RestartApplication(string returnUrl = null)
        {
            ViewBag.ReturnUrl = returnUrl ?? Services.WebHelper.GetUrlReferrer()?.PathAndQuery;
            return View();
        }

        [Permission(Permissions.System.Maintenance.Execute)]
        [HttpPost]
        public IActionResult RestartApplication()
        {
            Services.WebHelper.RestartAppDomain();
            return new EmptyResult();
        }

        [Permission(Permissions.System.Maintenance.Execute)]
        [HttpPost]
        public async Task<IActionResult> ClearCache()
        {
            // Clear Smartstore inbuilt cache
            await Services.Cache.ClearAsync();

            // Clear IMemoryCache Smartstore: region
            _memCache.RemoveByPattern(_memCache.BuildScopedKey("*"));

            return new JsonResult
            (
                new
                {
                    Success = true,
                    Message = T("Admin.Common.TaskSuccessfullyProcessed").Value
                }
            );
        }

        [Permission(Permissions.System.Maintenance.Execute)]
        [HttpPost]
        public async Task<IActionResult> ClearDatabaseCache()
        {
            var dbCache = _db.GetInfrastructure<IServiceProvider>().GetService<IDbCache>();
            if (dbCache != null)
            {
                await dbCache.ClearAsync();
            }

            return new JsonResult
            (
                new
                {
                    Success = true,
                    Message = T("Admin.Common.TaskSuccessfullyProcessed").Value
                }
            );
        }

        [Permission(Permissions.System.Maintenance.Read)]
        public async Task<IActionResult> SystemInfo()
        {
            var runtimeInfo = Services.ApplicationContext.RuntimeInfo;
            var dataProvider = _db.DataProvider;

            var model = new SystemInfoModel
            {
                AppVersion = SmartstoreVersion.CurrentFullVersion,
                ServerLocalTime = DateTime.Now,
                UtcTime = DateTime.UtcNow,
                ServerTimeZone = TimeZoneInfo.Local.StandardName,
                AspNetInfo = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                OperatingSystem = $"{runtimeInfo.OSDescription} ({runtimeInfo.ProcessArchitecture.ToString().ToLower()})"
            };

            // DB size & used RAM
            try
            {
                var mbSize = await dataProvider.GetDatabaseSizeAsync();
                model.DatabaseSize = Convert.ToInt64(mbSize * 1024 * 1024);
                model.UsedMemorySize = GetPrivateBytes();
            }
            catch
            {
            }

            // DB settings
            try
            {
                if (DataSettings.Instance.IsValid())
                {
                    model.DataProviderFriendlyName = DataSettings.Instance.DbFactory.DbSystem.ToString();
                    model.ShrinkDatabaseEnabled = dataProvider.CanShrink && Services.Permissions.Authorize(Permissions.System.Maintenance.Read);
                }
            }
            catch
            {
            }

            // Loaded assemblies
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var fi = new FileInfo(assembly.Location);
                model.AppDate = fi.LastWriteTime.ToLocalTime();
            }
            catch
            {
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var loadedAssembly = new SystemInfoModel.LoadedAssembly
                {
                    FullName = assembly.FullName
                };

                if (!assembly.IsDynamic)
                {
                    try
                    {
                        loadedAssembly.Location = assembly.Location;
                    }
                    catch
                    {

                    }
                }

                model.LoadedAssemblies.Add(loadedAssembly);
            }

            //// MemCache stats
            //model.MemoryCacheStats = GetMemoryCacheStats();

            return View(model);
        }

        [Permission(Permissions.System.Maintenance.Execute)]
        public async Task<IActionResult> GarbageCollect()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(500);
                NotifySuccess(T("Admin.System.SystemInfo.GarbageCollectSuccessful"));
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToReferrer();
        }

        [Permission(Permissions.System.Maintenance.Execute)]
        public async Task<IActionResult> ShrinkDatabase()
        {
            try
            {
                if (_db.DataProvider.CanShrink)
                {
                    await _db.DataProvider.ShrinkDatabaseAsync();
                    NotifySuccess(T("Common.ShrinkDatabaseSuccessful"));
                }
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToReferrer();
        }

        [Permission(Permissions.System.Maintenance.Read)]
        public async Task<IActionResult> Warnings()
        {
            var model = new List<SystemWarningModel>();
            var store = Services.StoreContext.CurrentStore;
            var appContext = Services.ApplicationContext;

            // Store URL
            // ====================================
            var storeUrl = store.Url.EnsureEndsWith('/');
            if (storeUrl.HasValue() && (storeUrl.EqualsNoCase(Services.WebHelper.GetStoreLocation(false)) || storeUrl.EqualsNoCase(Services.WebHelper.GetStoreLocation(true))))
            {
                AddEntry(SystemWarningLevel.Pass, T("Admin.System.Warnings.URL.Match"));
            }
            else
            {
                AddEntry(SystemWarningLevel.Warning, T("Admin.System.Warnings.URL.NoMatch", storeUrl, Services.WebHelper.GetStoreLocation(false)));
            }

            // TaskScheduler reachability
            // ====================================
            try
            {
                var taskSchedulerClient = await _taskScheduler.CreateHttpClientAsync();
                taskSchedulerClient.Timeout = TimeSpan.FromSeconds(5);

                using var response = await taskSchedulerClient.GetAsync("noop");
                response.EnsureSuccessStatusCode();

                var status = response.StatusCode;
                var warningModel = new SystemWarningModel
                {
                    Level = (status == HttpStatusCode.OK ? SystemWarningLevel.Pass : SystemWarningLevel.Fail)
                };

                if (status == HttpStatusCode.OK)
                {
                    warningModel.Text = T("Admin.System.Warnings.TaskScheduler.OK");
                }
                else
                {
                    warningModel.Text = T("Admin.System.Warnings.TaskScheduler.Fail", _taskScheduler.BaseUrl, status + " - " + status.ToString());
                }

                model.Add(warningModel);
            }
            catch (Exception exception)
            {
                var msg = T("Admin.System.Warnings.TaskScheduler.Fail", _taskScheduler.BaseUrl, exception.Message);
                AddEntry(SystemWarningLevel.Fail, msg);
                Logger.Error(exception, msg);
            }

            // Sitemap reachability
            // ====================================
            string sitemapUrl = null;
            try
            {
                var sitemapClient = _httpClientFactory.CreateClient();
                sitemapClient.Timeout = TimeSpan.FromSeconds(15);

                sitemapUrl = WebHelper.GetAbsoluteUrl(Url.Content("sitemap.xml"), Request);
                var uri = await WebHelper.CreateUriForSafeLocalCallAsync(new Uri(sitemapUrl));

                using var response = await sitemapClient.GetAsync(uri);
                response.EnsureSuccessStatusCode();

                var status = response.StatusCode;
                var warningModel = new SystemWarningModel
                {
                    Level = (status == HttpStatusCode.OK ? SystemWarningLevel.Pass : SystemWarningLevel.Warning)
                };

                switch (status)
                {
                    case HttpStatusCode.OK:
                        warningModel.Text = T("Admin.System.Warnings.SitemapReachable.OK");
                        break;
                    default:
                        if (status == HttpStatusCode.MethodNotAllowed)
                            warningModel.Text = T("Admin.System.Warnings.SitemapReachable.MethodNotAllowed");
                        else
                            warningModel.Text = T("Admin.System.Warnings.SitemapReachable.Wrong");

                        warningModel.Text = string.Concat(warningModel.Text, " ", T("Admin.Common.HttpStatus", (int)status, status.ToString()));
                        break;
                }

                model.Add(warningModel);
            }
            catch (Exception exception)
            {
                AddEntry(SystemWarningLevel.Warning, T("Admin.System.Warnings.SitemapReachable.Wrong"));
                Logger.Warn(exception, T("Admin.System.Warnings.SitemapReachable.Wrong"));
            }

            // Primary exchange rate currency
            // ====================================
            var perCurrency = _currencyService.Value.PrimaryExchangeCurrency;
            if (perCurrency != null)
            {
                AddEntry(SystemWarningLevel.Pass, T("Admin.System.Warnings.ExchangeCurrency.Set"));

                if (perCurrency.Rate != 1)
                {
                    AddEntry(SystemWarningLevel.Fail, T("Admin.System.Warnings.ExchangeCurrency.Rate1"));
                }
            }
            else
            {
                AddEntry(SystemWarningLevel.Fail, T("Admin.System.Warnings.ExchangeCurrency.NotSet"));
            }

            // Primary store currency
            // ====================================
            var pscCurrency = _currencyService.Value.PrimaryCurrency;
            if (pscCurrency != null)
            {
                AddEntry(SystemWarningLevel.Pass, T("Admin.System.Warnings.PrimaryCurrency.Set"));
            }
            else
            {
                AddEntry(SystemWarningLevel.Fail, T("Admin.System.Warnings.PrimaryCurrency.NotSet"));
            }


            // Base measure weight
            // ====================================
            var baseWeight = await _db.MeasureWeights.FindByIdAsync(_measureSettings.Value.BaseWeightId, false);
            if (baseWeight != null)
            {
                AddEntry(SystemWarningLevel.Pass, T("Admin.System.Warnings.DefaultWeight.Set"));

                if (baseWeight.Ratio != 1)
                {
                    AddEntry(SystemWarningLevel.Fail, T("Admin.System.Warnings.DefaultWeight.Ratio1"));
                }
            }
            else
            {
                AddEntry(SystemWarningLevel.Fail, T("Admin.System.Warnings.DefaultWeight.NotSet"));
            }


            // Base dimension weight
            // ====================================
            var baseDimension = await _db.MeasureDimensions.FindByIdAsync(_measureSettings.Value.BaseDimensionId, false);
            if (baseDimension != null)
            {
                AddEntry(SystemWarningLevel.Pass, T("Admin.System.Warnings.DefaultDimension.Set"));

                if (baseDimension.Ratio != 1)
                {
                    AddEntry(SystemWarningLevel.Fail, T("Admin.System.Warnings.DefaultDimension.Ratio1"));
                }
            }
            else
            {
                AddEntry(SystemWarningLevel.Fail, T("Admin.System.Warnings.DefaultDimension.NotSet"));
            }

            // Shipping rate coputation methods
            // ====================================
            int numActiveShippingMethods = 0;
            try
            {
                numActiveShippingMethods = _shippingService.Value.LoadActiveShippingRateComputationMethods()
                    .Where(x => x.Value.ShippingRateComputationMethodType == ShippingRateComputationMethodType.Offline)
                    .Count();
            }
            catch
            {
            }

            if (numActiveShippingMethods > 1)
            {
                AddEntry(SystemWarningLevel.Warning, T("Admin.System.Warnings.Shipping.OnlyOneOffline"));
            }

            // Payment methods
            // ====================================
            int numActivePaymentMethods = 0;
            try
            {
                numActivePaymentMethods = (await _paymentService.Value.LoadActivePaymentMethodsAsync()).Count();
            }
            catch
            {
            }

            if (numActivePaymentMethods > 0)
            {
                AddEntry(SystemWarningLevel.Pass, T("Admin.System.Warnings.PaymentMethods.OK"));
            }
            else
            {
                AddEntry(SystemWarningLevel.Fail, T("Admin.System.Warnings.PaymentMethods.NoActive"));
            }

            // Incompatible modules
            // ====================================
            foreach (var moduleName in appContext.ModuleCatalog.IncompatibleModules)
            {
                AddEntry(SystemWarningLevel.Warning, T("Admin.System.Warnings.IncompatiblePlugin", moduleName));
            }

            // Validate write permissions (the same procedure like during installation)
            // ====================================
            var dirPermissionsOk = true;
            foreach (var subpath in FilePermissionChecker.WrittenDirectories)
            {
                var entry = appContext.ContentRoot.GetDirectory(subpath);
                if (entry.Exists && !_filePermissionChecker.Value.CanAccess(entry, FileEntryRights.Write | FileEntryRights.Modify))
                {
                    AddEntry(SystemWarningLevel.Warning, T("Admin.System.Warnings.DirectoryPermission.Wrong", appContext.OSIdentity.Name, subpath));
                    dirPermissionsOk = false;
                }
            }
            if (dirPermissionsOk)
            {
                AddEntry(SystemWarningLevel.Pass, T("Admin.System.Warnings.DirectoryPermission.OK"));
            }

            var filePermissionsOk = true;
            foreach (var subpath in FilePermissionChecker.WrittenFiles)
            {
                var entry = appContext.ContentRoot.GetFile(subpath);
                if (entry.Exists && !_filePermissionChecker.Value.CanAccess(entry, FileEntryRights.Write | FileEntryRights.Modify | FileEntryRights.Delete))
                {
                    AddEntry(SystemWarningLevel.Warning, T("Admin.System.Warnings.FilePermission.Wrong", appContext.OSIdentity.Name, subpath));
                    filePermissionsOk = false;
                }
            }
            if (filePermissionsOk)
            {
                AddEntry(SystemWarningLevel.Pass, T("Admin.System.Warnings.FilePermission.OK"));
            }

            return View(model);

            void AddEntry(SystemWarningLevel level, string text)
            {
                model.Add(new SystemWarningModel { Level = level, Text = text });
            }
        }

        #region Database backup

        [Permission(Permissions.System.Maintenance.Read)]
        public async Task<IActionResult> BackupList()
        {
            var backups = await Services.ApplicationContext.TenantRoot
                .EnumerateFilesAsync(BACKUP_DIR)
                .AsyncToList();

            var rows = backups
                .Select(x =>
                {
                    var model = new DbBackupModel(x)
                    {
                        UpdatedOn = Services.DateTimeHelper.ConvertToUserTime(x.LastModified.UtcDateTime, DateTimeKind.Utc)
                    };

                    return model;
                })
                .ToList();

            return Json(new GridModel<DbBackupModel>
            {
                Rows = rows.OrderByDescending(x => x.UpdatedOn).ToList(),
                Total = rows.Count
            });
        }

        [HttpPost]
        [Permission(Permissions.System.Maintenance.Execute)]
        public async Task<IActionResult> CreateBackup()
        {
            try
            {
                if (_db.DataProvider.CanBackup)
                {
                    const string extension = ".bak";
                    var dir = await Services.ApplicationContext.TenantRoot.GetDirectoryAsync(BACKUP_DIR);
                    var fs = dir.FileSystem;

                    var dbName = _db.DataProvider.Database.GetDbConnection().Database.NaIfEmpty().ToValidFileName();
                    var fileName = $"{dbName}-{SmartstoreVersion.CurrentFullVersion}";
                    var i = 1;

                    for (; i < 10000; i++)
                    {
                        if (!await fs.FileExistsAsync(fs.PathCombine(dir.SubPath, $"{fileName}-{i}{extension}")))
                        {
                            break;
                        }
                    }

                    fileName = $"{fileName}-{i}{extension}";

                    var fullPath = fs.MapPath(fs.PathCombine(dir.SubPath, fileName));

                    await _db.DataProvider.BackupDatabaseAsync(fullPath);
                    // TODO: (mg) (core) notify
                }
                else
                {
                    // TODO: (mg) (core) notify about backup error.
                    NotifyError("");
                }
            }
            catch (Exception ex)
            {
                NotifyError(ex);
            }

            return RedirectToAction("Index");
        }

        [HttpPost]
        [Permission(Permissions.System.Maintenance.Execute)]
        public async Task<IActionResult> DeleteBackup(GridSelection selection)
        {
            var numDeleted = 0;
            var root = Services.ApplicationContext.TenantRoot;

            foreach (var fileName in selection.SelectedKeys)
            {
                if (await root.TryDeleteFileAsync(BACKUP_DIR + "\\" + fileName))
                {
                    numDeleted++;
                }
            }

            return Json(new { Success = true, Count = numDeleted });
        }

        [Permission(Permissions.System.Maintenance.Execute)]
        public async Task<IActionResult> DownloadBackup(string fileName)
        {
            if (PathUtility.HasInvalidFileNameChars(fileName))
            {
                throw new BadHttpRequestException("Invalid file name: " + fileName.NaIfEmpty());
            }

            var root = Services.ApplicationContext.TenantRoot;
            var backup = await root.GetFileAsync(BACKUP_DIR + "\\" + fileName);
            var contentType = MimeTypes.MapNameToMimeType(backup.PhysicalPath);

            try
            {
                return new FileStreamResult(backup.OpenRead(), contentType)
                {
                    FileDownloadName = fileName
                };
            }
            catch (IOException)
            {
                NotifyWarning(T("Admin.Common.FileInUse"));
            }

            return RedirectToAction("Index");
        }

        #endregion

        #region Utils

        /// <summary>
        /// Counts the size of all objects in both IMemoryCache and Smartstore memory cache
        /// </summary>
        private IDictionary<string, long> GetMemoryCacheStats()
        {
            var cache = Services.CacheFactory.GetMemoryCache();
            var stats = new Dictionary<string, long>();
            var instanceLookups = new HashSet<object>(ReferenceEqualityComparer.Instance) { cache, _memCache };

            // IMemoryCache
            var memCacheKeys = _memCache.EnumerateKeys().ToArray();
            foreach (var key in memCacheKeys)
            {
                var value = _memCache.Get(key);
                var size = GetObjectSize(value);

                if (key is string str)
                {
                    stats.Add("MemoryCache:" + str.Replace(':', '_'), size + (sizeof(char) + (str.Length + 1)));
                }
                else
                {
                    stats.Add("MemoryCache:" + key.ToString(), size + GetObjectSize(key));
                }
            }

            // Smartstore CacheManager
            var cacheKeys = cache.Keys("*").ToArray();
            foreach (var key in cacheKeys)
            {
                var value = cache.Get<object>(key);
                var size = GetObjectSize(value);

                stats.Add(key, size + (sizeof(char) + (key.Length + 1)));
            }

            return stats;

            long GetObjectSize(object obj)
            {
                if (obj == null)
                {
                    return 0;
                }
                
                try
                {
                    return CommonHelper.GetObjectSizeInBytes(obj, instanceLookups);
                }
                catch
                {
                    return 0;
                }
            }
        }

        private static long GetPrivateBytes()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var process = Process.GetCurrentProcess();
            process.Refresh();

            return process.PrivateMemorySize64;
        }

        #endregion
    }
}