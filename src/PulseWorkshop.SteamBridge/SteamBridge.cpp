// The Steamworks headers use classic CRT string functions; silence their deprecation warnings.
// Must precede any CRT/SDK include.
#ifndef _CRT_SECURE_NO_WARNINGS
#define _CRT_SECURE_NO_WARNINGS
#endif

#include "SteamBridge.h"

// Steamworks SDK — provided out-of-band, see README (external/steamworks_sdk/public).
#include "steam/steam_api.h"

#include <msclr/marshal_cppstd.h>
#include <string>
#include <vector>

using namespace System;
using namespace System::Collections::Generic;
using namespace System::Runtime::InteropServices;
using namespace msclr::interop;

namespace PulseWorkshop {
namespace SteamBridge {

namespace {

    // Convert a managed string to a native UTF-8 std::string (empty for nullptr).
    std::string ToNative(String^ s)
    {
        if (s == nullptr)
            return std::string();
        marshal_context ctx;
        // marshal_as gives ANSI; for Steam (UTF-8) convert explicitly.
        array<unsigned char>^ bytes = System::Text::Encoding::UTF8->GetBytes(s);
        if (bytes->Length == 0)
            return std::string();
        std::string out(bytes->Length, '\0');
        Marshal::Copy(bytes, 0, IntPtr(&out[0]), bytes->Length);
        return out;
    }

    String^ ToManaged(const char* s)
    {
        if (s == nullptr)
            return String::Empty;
        int len = static_cast<int>(strlen(s));
        if (len == 0)
            return String::Empty;
        array<unsigned char>^ bytes = gcnew array<unsigned char>(len);
        Marshal::Copy(IntPtr(const_cast<char*>(s)), bytes, 0, len);
        return System::Text::Encoding::UTF8->GetString(bytes);
    }

    // Blocks pumping callbacks until the given API call completes, then copies the result.
    // Returns false on IO failure / timeout.
    template <typename TResult>
    bool WaitForCall(SteamAPICall_t call, TResult& outResult, int timeoutMs = 30000)
    {
        if (call == k_uAPICallInvalid)
            return false;

        ISteamUtils* utils = SteamUtils();
        bool failed = false;
        int waited = 0;
        while (!SteamUtils()->IsAPICallCompleted(call, &failed))
        {
            SteamAPI_RunCallbacks();
            ::Sleep(10);
            waited += 10;
            if (waited >= timeoutMs)
                return false;
        }

        bool ioFailure = false;
        if (!utils->GetAPICallResult(call, &outResult, sizeof(TResult),
                                     TResult::k_iCallback, &ioFailure))
            return false;

        return !ioFailure && !failed;
    }

    BridgeVisibility ToBridgeVisibility(ERemoteStoragePublishedFileVisibility v)
    {
        switch (v)
        {
        case k_ERemoteStoragePublishedFileVisibilityPublic: return BridgeVisibility::Public;
        case k_ERemoteStoragePublishedFileVisibilityFriendsOnly: return BridgeVisibility::FriendsOnly;
        case k_ERemoteStoragePublishedFileVisibilityUnlisted: return BridgeVisibility::Unlisted;
        default: return BridgeVisibility::Private;
        }
    }

    ERemoteStoragePublishedFileVisibility ToSteamVisibility(BridgeVisibility v)
    {
        switch (v)
        {
        case BridgeVisibility::Public: return k_ERemoteStoragePublishedFileVisibilityPublic;
        case BridgeVisibility::FriendsOnly: return k_ERemoteStoragePublishedFileVisibilityFriendsOnly;
        case BridgeVisibility::Unlisted: return k_ERemoteStoragePublishedFileVisibilityUnlisted;
        default: return k_ERemoteStoragePublishedFileVisibilityPrivate;
        }
    }

} // anonymous namespace

SteamWorkshop::SteamWorkshop()
    : _initialized(false), _activeUpdateHandle(0)
{
}

SteamWorkshop::~SteamWorkshop()
{
    this->!SteamWorkshop();
}

SteamWorkshop::!SteamWorkshop()
{
    Shutdown();
}

bool SteamWorkshop::Init()
{
    if (_initialized)
        return true;

    // SteamAPI_Init connects to the already-running Steam client and succeeds only if the
    // logged-in account owns the app — this is what gives us "no separate login prompt".
    if (!SteamAPI_Init())
        return false;

    // Give the client a moment to establish the content/UGC connection before any upload, pumping
    // callbacks so the connection handshake completes (avoids EResult NoConnection/Fail on upload).
    for (int i = 0; i < 50; ++i)
    {
        SteamAPI_RunCallbacks();
        ::Sleep(20);
    }

    _initialized = true;
    return true;
}

void SteamWorkshop::Shutdown()
{
    if (_initialized)
    {
        SteamAPI_Shutdown();
        _initialized = false;
    }
}

void SteamWorkshop::RunCallbacks()
{
    if (_initialized)
        SteamAPI_RunCallbacks();
}

BridgePing^ SteamWorkshop::Ping()
{
    BridgePing^ ping = gcnew BridgePing();
    ping->SteamRunning = _initialized;
    if (!_initialized)
        return ping;

    ISteamUser* user = SteamUser();
    ISteamFriends* friends = SteamFriends();
    ping->SteamId = user ? user->GetSteamID().ConvertToUint64() : 0;
    ping->PersonaName = friends ? ToManaged(friends->GetPersonaName()) : String::Empty;
    ping->AppId = SteamUtils() ? SteamUtils()->GetAppID() : 0;
    return ping;
}

BridgeQueryResult^ SteamWorkshop::QueryUserPublished(int page)
{
    BridgeQueryResult^ result = gcnew BridgeQueryResult();
    result->Items = gcnew List<BridgeItem^>();
    result->TotalResults = 0;

    if (!_initialized)
        return result;

    ISteamUGC* ugc = SteamUGC();
    AccountID_t account = SteamUser()->GetSteamID().GetAccountID();
    AppId_t appId = SteamUtils()->GetAppID();

    UGCQueryHandle_t handle = ugc->CreateQueryUserUGCRequest(
        account,
        k_EUserUGCList_Published,
        k_EUGCMatchingUGCType_All,
        k_EUserUGCListSortOrder_LastUpdatedDesc,
        appId, appId,
        static_cast<uint32>(page < 1 ? 1 : page));

    if (handle == k_UGCQueryHandleInvalid)
        return result;

    ugc->SetReturnLongDescription(handle, true);

    SteamAPICall_t call = ugc->SendQueryUGCRequest(handle);
    SteamUGCQueryCompleted_t completed;
    if (!WaitForCall(call, completed) ||
        completed.m_eResult != k_EResultOK)
    {
        ugc->ReleaseQueryUGCRequest(handle);
        return result;
    }

    result->TotalResults = static_cast<int>(completed.m_unTotalMatchingResults);

    for (uint32 i = 0; i < completed.m_unNumResultsReturned; ++i)
    {
        SteamUGCDetails_t details;
        if (!ugc->GetQueryUGCResult(handle, i, &details) ||
            details.m_eResult != k_EResultOK)
            continue;

        BridgeItem^ item = gcnew BridgeItem();
        item->PublishedFileId = details.m_nPublishedFileId;
        item->Title = ToManaged(details.m_rgchTitle);
        item->Description = ToManaged(details.m_rgchDescription);
        item->Visibility = ToBridgeVisibility(details.m_eVisibility);
        item->UpdatedUnix = static_cast<Int64>(details.m_rtimeUpdated);
        item->CreatedUnix = static_cast<Int64>(details.m_rtimeCreated);
        // Prefer the accurate non-legacy total; fall back to the legacy single-file size.
        item->FileSize = details.m_ulTotalFilesSize != 0
            ? details.m_ulTotalFilesSize
            : static_cast<UInt64>(details.m_nFileSize < 0 ? 0 : details.m_nFileSize);
        // Cloud filename of the primary content file (legacy RemoteStorage items have one).
        item->ContentFileName = ToManaged(details.m_pchFileName);

        // Tags arrive as a comma-separated string on the details struct.
        item->Tags = gcnew List<String^>();
        String^ tagCsv = ToManaged(details.m_rgchTags);
        if (!String::IsNullOrEmpty(tagCsv))
        {
            for each (String ^ t in tagCsv->Split(','))
            {
                String^ trimmed = t->Trim();
                if (trimmed->Length > 0)
                    item->Tags->Add(trimmed);
            }
        }

        char previewUrl[1024] = { 0 };
        if (ugc->GetQueryUGCPreviewURL(handle, i, previewUrl, sizeof(previewUrl)))
            item->PreviewUrl = ToManaged(previewUrl);

        result->Items->Add(item);
    }

    ugc->ReleaseQueryUGCRequest(handle);
    return result;
}

namespace {

    // Returns just the file name portion of a path (handles both \ and /).
    std::string FileNameOf(const std::string& path)
    {
        auto slash = path.find_last_of("\\/");
        return (slash == std::string::npos) ? path : path.substr(slash + 1);
    }

    // Uploads a local file into Steam Cloud (Remote Storage) under cloudName, streamed in chunks so
    // large files (hundreds of MB) don't need to fit in memory. Returns false with an error message.
    // Per-chunk progress is written to stderr (label distinguishes "content" / "preview") so the App
    // can surface a live upload log instead of a static "uploading..." message.
    bool UploadToCloud(const std::string& localPath, const std::string& cloudName,
                       const std::string& label, std::string& error)
    {
        ISteamRemoteStorage* rs = SteamRemoteStorage();

        FILE* f = nullptr;
        if (fopen_s(&f, localPath.c_str(), "rb") != 0 || f == nullptr)
        {
            error = "Could not open content file: " + localPath;
            return false;
        }

        // Total size up front so progress can be reported as a percentage.
        _fseeki64(f, 0, SEEK_END);
        long long totalBytes = _ftelli64(f);
        _fseeki64(f, 0, SEEK_SET);
        if (totalBytes < 0)
            totalBytes = 0;

        UGCFileWriteStreamHandle_t stream = rs->FileWriteStreamOpen(cloudName.c_str());
        if (stream == k_UGCFileStreamHandleInvalid)
        {
            fclose(f);
            error = "FileWriteStreamOpen failed (Steam Cloud may be disabled or out of quota).";
            return false;
        }

        const double mb = 1024.0 * 1024.0;
        String^ tag = gcnew String(label.c_str());
        Console::Error->WriteLine(String::Format(
            "[upload] {0}: uploading {1:0.0} MB to Steam Cloud...", tag, totalBytes / mb));

        const int chunkSize = 1 << 20; // 1 MB
        std::vector<char> buffer(chunkSize);
        bool ok = true;
        size_t read = 0;
        long long written = 0;
        int lastPercent = -1;
        while ((read = fread(buffer.data(), 1, chunkSize, f)) > 0)
        {
            if (!rs->FileWriteStreamWriteChunk(stream, buffer.data(), static_cast<int32>(read)))
            {
                ok = false;
                error = "FileWriteStreamWriteChunk failed (likely Steam Cloud quota exceeded).";
                break;
            }
            SteamAPI_RunCallbacks();

            written += static_cast<long long>(read);
            int percent = totalBytes > 0 ? static_cast<int>((written * 100) / totalBytes) : 100;
            if (percent != lastPercent)
            {
                lastPercent = percent;
                Console::Error->WriteLine(String::Format(
                    "[upload] {0}: {1:0.0} / {2:0.0} MB ({3}%)", tag, written / mb, totalBytes / mb, percent));
            }
        }
        fclose(f);

        if (!ok)
        {
            rs->FileWriteStreamClose(stream);
            return false;
        }

        if (!rs->FileWriteStreamClose(stream))
        {
            error = "FileWriteStreamClose failed.";
            return false;
        }
        Console::Error->WriteLine(String::Format("[upload] {0}: upload complete.", tag));
        return true;
    }

    void BuildTagArray(System::Collections::Generic::List<System::String^>^ tags,
                       std::vector<std::string>& store, std::vector<const char*>& ptrs,
                       SteamParamStringArray_t& out)
    {
        if (tags != nullptr)
        {
            store.reserve(tags->Count);
            for each (System::String ^ t in tags)
                store.push_back(ToNative(t));
            for (auto& s : store)
                ptrs.push_back(s.c_str());
        }
        out.m_ppStrings = ptrs.empty() ? nullptr : ptrs.data();
        out.m_nNumStrings = static_cast<int32>(ptrs.size());
    }

} // anonymous namespace

BridgePublishResult^ SteamWorkshop::Publish(BridgeEdit^ edit)
{
    BridgePublishResult^ result = gcnew BridgePublishResult();
    result->Success = false;
    if (!_initialized)
    {
        result->Error = "Steam is not initialized.";
        return result;
    }

    ISteamRemoteStorage* rs = SteamRemoteStorage();
    AppId_t appId = SteamUtils()->GetAppID();

    // L4D2 / GMod use the legacy Steam Cloud (RemoteStorage) Workshop - there is no upload depot,
    // so ISteamUGC::SubmitItemUpdate fails with "no workshop depot found". Mirror Crowbar: push the
    // content (and preview) into Steam Cloud, then publish/update the item referencing them.

    bool hasContent = !String::IsNullOrEmpty(edit->ContentFile);
    bool hasPreview = !String::IsNullOrEmpty(edit->PreviewImagePath);

    // Cloud staging names. Use the content file's real filename (like Crowbar) so the game gets
    // the correct addon name/extension.
    std::string contentCloud = hasContent ? FileNameOf(ToNative(edit->ContentFile)) : "";
    std::string previewCloud = hasPreview ? ("preview_" + FileNameOf(ToNative(edit->PreviewImagePath))) : "";

    // Quota check before a potentially large upload.
    if (hasContent)
    {
        uint64 totalBytes = 0, availBytes = 0;
        if (rs->GetQuota(&totalBytes, &availBytes))
        {
            FILE* f = nullptr;
            if (fopen_s(&f, ToNative(edit->ContentFile).c_str(), "rb") == 0 && f)
            {
                _fseeki64(f, 0, SEEK_END);
                long long sz = _ftelli64(f);
                fclose(f);
                if (sz > 0 && static_cast<uint64>(sz) > availBytes)
                {
                    result->Error = "Steam Cloud quota too low for this content (" +
                        (static_cast<UInt64>(sz)).ToString() + " bytes needed, " +
                        (static_cast<UInt64>(availBytes)).ToString() + " available).";
                    return result;
                }
            }
        }

        std::string err;
        if (!UploadToCloud(ToNative(edit->ContentFile), contentCloud, "content", err))
        {
            result->Error = gcnew String(err.c_str());
            return result;
        }

        // Confirm Steam registered the Cloud file before we reference it in the publish.
        if (!rs->FileExists(contentCloud.c_str()))
        {
            result->Error = "Content was written to Steam Cloud but not registered (FileExists=false).";
            return result;
        }
        Console::Error->WriteLine(String::Format("[bridge] content cloud file '{0}' size={1}",
            gcnew String(contentCloud.c_str()), (Int32)rs->GetFileSize(contentCloud.c_str())));
    }

    if (hasPreview)
    {
        std::string err;
        if (!UploadToCloud(ToNative(edit->PreviewImagePath), previewCloud, "preview", err))
        {
            result->Error = "Preview upload failed: " + gcnew String(err.c_str());
            return result;
        }
    }

    PublishedFileId_t fileId = static_cast<PublishedFileId_t>(edit->PublishedFileId);

    if (fileId == 0)
    {
        // --- Publish a brand-new item ---
        Console::Error->WriteLine("[publish] creating new workshop item...");
        std::vector<std::string> tagStore;
        std::vector<const char*> tagPtrs;
        SteamParamStringArray_t tags;
        BuildTagArray(edit->Tags, tagStore, tagPtrs, tags);

        std::string title = ToNative(edit->Title);
        std::string desc = ToNative(edit->Description);

        SteamAPICall_t call = rs->PublishWorkshopFile(
            hasContent ? contentCloud.c_str() : nullptr,
            hasPreview ? previewCloud.c_str() : nullptr,
            appId, title.c_str(), desc.c_str(),
            ToSteamVisibility(edit->Visibility),
            (tags.m_nNumStrings > 0) ? &tags : nullptr,
            k_EWorkshopFileTypeCommunity);

        RemoteStoragePublishFileResult_t published;
        if (!WaitForCall(call, published, 1800000))
        {
            result->Error = "Timed out publishing the workshop file.";
            return result;
        }

        result->PublishedFileId = static_cast<UInt64>(published.m_nPublishedFileId);
        result->NeedsLegalAgreement = published.m_bUserNeedsToAcceptWorkshopLegalAgreement;
        result->Success = (published.m_eResult == k_EResultOK);
        if (!result->Success)
            result->Error = "Publish failed (EResult " + (static_cast<int>(published.m_eResult)).ToString() + ").";
    }
    else
    {
        // --- Update an existing item ---
        Console::Error->WriteLine(String::Format(
            "[publish] committing update to item {0}...", static_cast<UInt64>(fileId)));
        result->PublishedFileId = static_cast<UInt64>(fileId);
        PublishedFileUpdateHandle_t handle = rs->CreatePublishedFileUpdateRequest(fileId);

        if (!String::IsNullOrEmpty(edit->Title))
            rs->UpdatePublishedFileTitle(handle, ToNative(edit->Title).c_str());
        if (edit->Description != nullptr)
            rs->UpdatePublishedFileDescription(handle, ToNative(edit->Description).c_str());
        rs->UpdatePublishedFileVisibility(handle, ToSteamVisibility(edit->Visibility));

        if (hasContent)
            rs->UpdatePublishedFileFile(handle, contentCloud.c_str());
        if (hasPreview)
            rs->UpdatePublishedFilePreviewFile(handle, previewCloud.c_str());

        std::vector<std::string> tagStore;
        std::vector<const char*> tagPtrs;
        SteamParamStringArray_t tags;
        BuildTagArray(edit->Tags, tagStore, tagPtrs, tags);
        if (tags.m_nNumStrings > 0)
            rs->UpdatePublishedFileTags(handle, &tags);

        std::string note = ToNative(edit->ChangeNote);
        if (!note.empty())
            rs->UpdatePublishedFileSetChangeDescription(handle, note.c_str());

        SteamAPICall_t call = rs->CommitPublishedFileUpdate(handle);
        RemoteStorageUpdatePublishedFileResult_t updated;
        if (!WaitForCall(call, updated, 1800000))
        {
            result->Error = "Timed out updating the workshop file.";
            return result;
        }

        result->NeedsLegalAgreement = updated.m_bUserNeedsToAcceptWorkshopLegalAgreement;
        result->Success = (updated.m_eResult == k_EResultOK);
        if (!result->Success)
            result->Error = "Update failed (EResult " + (static_cast<int>(updated.m_eResult)).ToString() + ").";
    }

    // NOTE: do NOT FileDelete the Cloud staging files here. Steam promotes them to the item's UGC
    // copy asynchronously after publish; deleting immediately can race that promotion and leave the
    // item with 0 bytes of content. Crowbar likewise leaves them. (A later cleanup pass could remove
    // stale staging files once promotion is confirmed.)

    return result;
}

BridgeDeleteResult^ SteamWorkshop::DeletePublishedFile(System::UInt64 publishedFileId)
{
    BridgeDeleteResult^ result = gcnew BridgeDeleteResult();
    result->Success = false;
    if (!_initialized)
    {
        result->Error = "Steam is not initialized.";
        return result;
    }
    if (publishedFileId == 0)
    {
        result->Error = "No published file id supplied.";
        return result;
    }

    // L4D2 / GMod are legacy RemoteStorage Workshop items, so deletion goes through
    // ISteamRemoteStorage::DeletePublishedFile (the same interface that published them).
    ISteamRemoteStorage* rs = SteamRemoteStorage();
    PublishedFileId_t fileId = static_cast<PublishedFileId_t>(publishedFileId);

    Console::Error->WriteLine(String::Format(
        "[delete] deleting workshop item {0}...", static_cast<UInt64>(fileId)));

    SteamAPICall_t call = rs->DeletePublishedFile(fileId);
    RemoteStorageDeletePublishedFileResult_t deleted;
    if (!WaitForCall(call, deleted))
    {
        result->Error = "Timed out deleting the workshop file.";
        return result;
    }

    result->Success = (deleted.m_eResult == k_EResultOK);
    if (!result->Success)
        result->Error = "Delete failed (EResult " + (static_cast<int>(deleted.m_eResult)).ToString() + ").";
    return result;
}

BridgeProgress^ SteamWorkshop::GetProgress()
{
    // The RemoteStorage upload runs synchronously inside Publish(), so there is no separate
    // in-flight progress to poll. Reported as done; the App shows status via the publish result.
    BridgeProgress^ progress = gcnew BridgeProgress();
    progress->Status = "idle";
    progress->Done = true;
    return progress;
}

}
}
