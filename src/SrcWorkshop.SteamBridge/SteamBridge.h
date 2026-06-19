#pragma once

// Managed (C++/CLI) surface over the native Steamworks ISteamUGC API. Referenced by
// SrcWorkshop.SteamHost. Keeps the native SDK isolated from the rest of the (pure C#) solution.
//
// Requires the Steamworks SDK headers at external/steamworks_sdk/public (see README).
//
// NOTE: this header deliberately fully-qualifies managed types (System::String^, etc.) and does
// NOT do `using namespace System;`. The Win32 COM headers pulled in by steam_api.h define their
// own IServiceProvider, which collides with System::IServiceProvider when System is in scope.

namespace SrcWorkshop {
namespace SteamBridge {

    public enum class BridgeVisibility
    {
        Public = 0,
        FriendsOnly = 1,
        Private = 2,
        Unlisted = 3,
    };

    /// <summary>A published Workshop item returned by a query.</summary>
    public ref class BridgeItem
    {
    public:
        property System::UInt64 PublishedFileId;
        property System::String^ Title;
        property System::String^ Description;
        property System::Collections::Generic::List<System::String^>^ Tags;
        property BridgeVisibility Visibility;
        property System::String^ PreviewUrl;
        property System::Int64 UpdatedUnix;   // seconds since epoch, 0 if unknown
        property System::Int64 CreatedUnix;   // initial publish time, 0 if unknown
        property System::UInt64 FileSize;     // total size of all files, bytes
    };

    /// <summary>One page of query results.</summary>
    public ref class BridgeQueryResult
    {
    public:
        property System::Collections::Generic::List<BridgeItem^>^ Items;
        property int TotalResults;
    };

    /// <summary>Result of creating/submitting an item update.</summary>
    public ref class BridgePublishResult
    {
    public:
        property System::UInt64 PublishedFileId;
        property bool NeedsLegalAgreement;
    };

    /// <summary>The fields to write when creating or updating an item.</summary>
    public ref class BridgeEdit
    {
    public:
        property System::UInt64 PublishedFileId;   // 0 to create a new item
        property System::String^ Title;
        property System::String^ Description;
        property System::Collections::Generic::List<System::String^>^ Tags;
        property BridgeVisibility Visibility;
        property System::String^ ContentFolder;    // absolute path; may be null
        property System::String^ PreviewImagePath; // absolute path; may be null
        property System::String^ ChangeNote;
    };

    public ref class BridgeProgress
    {
    public:
        property System::UInt64 BytesProcessed;
        property System::UInt64 BytesTotal;
        property System::String^ Status;
        property bool Done;
    };

    public ref class BridgePing
    {
    public:
        property bool SteamRunning;
        property System::UInt64 SteamId;
        property System::String^ PersonaName;
        property unsigned int AppId;
    };

    /// <summary>
    /// Thin wrapper over the Steamworks API. One instance owns one initialized Steam session;
    /// the host sets the App ID (steam_appid.txt / SteamAppId) before calling <see cref="Init"/>.
    /// </summary>
    public ref class SteamWorkshop
    {
    public:
        SteamWorkshop();
        ~SteamWorkshop();
        !SteamWorkshop();

        /// <summary>Initializes the Steam session for the current process App ID. Returns false
        /// if Steam is not running or the game is not owned.</summary>
        bool Init();

        void Shutdown();

        /// <summary>Pumps queued Steam callbacks. Cheap; safe to call when idle.</summary>
        void RunCallbacks();

        BridgePing^ Ping();

        /// <summary>Queries the logged-in user's published items for the active app (1-based page).</summary>
        BridgeQueryResult^ QueryUserPublished(int page);

        /// <summary>Creates (if PublishedFileId==0) or updates an item and starts the upload.</summary>
        BridgePublishResult^ Publish(BridgeEdit^ edit);

        /// <summary>Progress of the most recent <see cref="Publish"/> upload.</summary>
        BridgeProgress^ GetProgress();

    private:
        bool _initialized;
        // Handle of the in-flight update, so GetProgress can report on it.
        unsigned long long _activeUpdateHandle;
    };

}
}
