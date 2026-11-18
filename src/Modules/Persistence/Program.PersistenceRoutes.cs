using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // HTTP routes owned by the Persistence module, in their former router order (see RouteTable.cs).
        private static void RegisterPersistenceRoutes(ReignRouteTable routes)
        {
            // Save Sync replaces campaign data and takes the campaign gate itself.
            routes.CampaignGateExemptArea("/save-sync/");
            routes.CampaignGateExemptArea("/api/save-sync/");
            routes.Get("/api/campaigns", request => ListCampaigns());
            routes.Get("/api/campaigns/storage-audit", request => CampaignStorageAuditApi());
            // Campaign backup export, import and deletion take the campaign gate themselves.
            routes.Get("/api/campaigns/export", (request, route) =>
            {
                WriteCampaignBackupDownload(route.Stream, request.Query);
                LogRequest(request, 200);
                return RouteResponseWritten;
            }).WithoutCampaignGate();
            routes.Post("/api/campaigns/import", request => ImportCampaignBackup(request.UploadFilePath, request.Query)).WithoutCampaignGate();
            routes.Post("/api/campaigns/delete", request => DeleteCampaignBackupData(request.JsonBody)).WithoutCampaignGate();
            routes.Post("/api/campaigns/delete-all", (request, route) =>
            {
                // Only the request that published the pending owner signals its response.
                return DeleteAllCampaignBackupData(request.JsonBody, out route.DeleteAllResponsePending);
            }).WithoutCampaignGate();
            routes.Post("/api/campaigns/cleanup-zero-save", request => CleanupZeroSaveCampaignsApi(request.JsonBody)).WithoutCampaignGate();
            routes.Post("/save-sync/register", request => SaveSyncRegisterApi(request.JsonBody));
            routes.Post("/save-sync/finalize", request => SaveSyncFinalizeApi(request.JsonBody));
            routes.Post("/save-sync/delete-native-save", request => SaveSyncDeleteNativeSaveApi(request.JsonBody));
            routes.Post("/save-sync/campaign-unloaded", request => CampaignUnloadedApi(request.JsonBody));
            routes.Post("/save-sync/load", request => SaveSyncLoadApi(request.JsonBody, false));
            routes.Post("/api/save-sync/preview", request => SaveSyncLoadApi(request.JsonBody, true));
            routes.Get("/api/save-sync/status", request => SaveSyncStatusApi(request.Query));
        }
    }
}
