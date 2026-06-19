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

namespace SrcWorkshop {
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

BridgePublishResult^ SteamWorkshop::Publish(BridgeEdit^ edit)
{
    BridgePublishResult^ result = gcnew BridgePublishResult();
    if (!_initialized)
        return result;

    ISteamUGC* ugc = SteamUGC();
    AppId_t appId = SteamUtils()->GetAppID();
    PublishedFileId_t fileId = static_cast<PublishedFileId_t>(edit->PublishedFileId);

    // Create a new item first if needed.
    if (fileId == 0)
    {
        SteamAPICall_t createCall = ugc->CreateItem(appId, k_EWorkshopFileTypeCommunity);
        CreateItemResult_t created;
        if (!WaitForCall(createCall, created) || created.m_eResult != k_EResultOK)
            return result;

        fileId = created.m_nPublishedFileId;
        result->NeedsLegalAgreement = created.m_bUserNeedsToAcceptWorkshopLegalAgreement;
    }

    result->PublishedFileId = static_cast<UInt64>(fileId);

    UGCUpdateHandle_t update = ugc->StartItemUpdate(appId, fileId);
    _activeUpdateHandle = update;

    if (!String::IsNullOrEmpty(edit->Title))
        ugc->SetItemTitle(update, ToNative(edit->Title).c_str());

    if (edit->Description != nullptr)
        ugc->SetItemDescription(update, ToNative(edit->Description).c_str());

    ugc->SetItemVisibility(update, ToSteamVisibility(edit->Visibility));

    if (edit->Tags != nullptr)
    {
        std::vector<std::string> tagStore;
        tagStore.reserve(edit->Tags->Count);
        for each (String ^ t in edit->Tags)
            tagStore.push_back(ToNative(t));

        std::vector<const char*> tagPtrs;
        tagPtrs.reserve(tagStore.size());
        for (auto& s : tagStore)
            tagPtrs.push_back(s.c_str());

        SteamParamStringArray_t tags;
        tags.m_ppStrings = tagPtrs.empty() ? nullptr : tagPtrs.data();
        tags.m_nNumStrings = static_cast<int32>(tagPtrs.size());
        ugc->SetItemTags(update, &tags);
    }

    if (!String::IsNullOrEmpty(edit->ContentFolder))
        ugc->SetItemContent(update, ToNative(edit->ContentFolder).c_str());

    if (!String::IsNullOrEmpty(edit->PreviewImagePath))
        ugc->SetItemPreview(update, ToNative(edit->PreviewImagePath).c_str());

    std::string note = ToNative(edit->ChangeNote);
    SteamAPICall_t submitCall = ugc->SubmitItemUpdate(update, note.empty() ? nullptr : note.c_str());

    SubmitItemUpdateResult_t submitted;
    if (WaitForCall(submitCall, submitted, 120000))
    {
        if (submitted.m_bUserNeedsToAcceptWorkshopLegalAgreement)
            result->NeedsLegalAgreement = true;
    }

    _activeUpdateHandle = 0;
    return result;
}

BridgeProgress^ SteamWorkshop::GetProgress()
{
    BridgeProgress^ progress = gcnew BridgeProgress();
    progress->Status = "idle";

    if (!_initialized || _activeUpdateHandle == 0)
    {
        progress->Done = true;
        return progress;
    }

    uint64 processed = 0, total = 0;
    EItemUpdateStatus status = SteamUGC()->GetItemUpdateProgress(
        _activeUpdateHandle, &processed, &total);

    progress->BytesProcessed = processed;
    progress->BytesTotal = total;
    progress->Done = (status == k_EItemUpdateStatusInvalid);

    switch (status)
    {
    case k_EItemUpdateStatusPreparingConfig:  progress->Status = "preparing"; break;
    case k_EItemUpdateStatusPreparingContent: progress->Status = "preparing content"; break;
    case k_EItemUpdateStatusUploadingContent: progress->Status = "uploading content"; break;
    case k_EItemUpdateStatusUploadingPreviewFile: progress->Status = "uploading preview"; break;
    case k_EItemUpdateStatusCommittingChanges: progress->Status = "committing"; break;
    default: progress->Status = "done"; break;
    }

    return progress;
}

}
}
