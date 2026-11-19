using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static class PersistenceRoutes
    {
        // HTTP routes owned by the Persistence module, in their former router order (see RouteTable.cs).
        internal static void RegisterPersistenceRoutes(Program.ReignRouteTable routes)
        {
            // Save Sync replaces campaign data and takes the campaign gate itself.
            routes.CampaignGateExemptArea("/save-sync/");
            routes.CampaignGateExemptArea("/api/save-sync/");
            routes.Get("/api/campaigns", request => Program.ListCampaigns());
            routes.Get("/api/campaigns/storage-audit", request => PersistenceService.CampaignStorageAuditApi());
            // Campaign backup export, import and deletion take the campaign gate themselves.
            routes.Get("/api/campaigns/export", (request, route) =>
            {
                PersistenceService.WriteCampaignBackupDownload(route.Stream, request.Query);
                Program.LogRequest(request, 200);
                return Program.RouteResponseWritten;
            }).WithoutCampaignGate();
            routes.Post("/api/campaigns/import", request => PersistenceService.ImportCampaignBackup(request.UploadFilePath, request.Query)).WithoutCampaignGate();
            routes.Post("/api/campaigns/delete", request => PersistenceService.DeleteCampaignBackupData(request.JsonBody)).WithoutCampaignGate();
            routes.Post("/api/campaigns/delete-all", (request, route) =>
            {
                // Only the request that published the pending owner signals its response.
                return PersistenceService.DeleteAllCampaignBackupData(request.JsonBody, out route.DeleteAllResponsePending);
            }).WithoutCampaignGate();
            routes.Post("/api/campaigns/cleanup-zero-save", request => PersistenceService.CleanupZeroSaveCampaignsApi(request.JsonBody)).WithoutCampaignGate();
            routes.Post("/save-sync/register", request => PersistenceService.SaveSyncRegisterApi(request.JsonBody));
            routes.Post("/save-sync/finalize", request => PersistenceService.SaveSyncFinalizeApi(request.JsonBody));
            routes.Post("/save-sync/delete-native-save", request => PersistenceService.SaveSyncDeleteNativeSaveApi(request.JsonBody));
            routes.Post("/save-sync/campaign-unloaded", request => PersistenceService.CampaignUnloadedApi(request.JsonBody));
            routes.Post("/save-sync/load", request => PersistenceService.SaveSyncLoadApi(request.JsonBody, false));
            routes.Post("/api/save-sync/preview", request => PersistenceService.SaveSyncLoadApi(request.JsonBody, true));
            routes.Get("/api/save-sync/status", request => PersistenceService.SaveSyncStatusApi(request.Query));
        }
    }
}
