//------------------------------------------------------------------------------
// Strongly-typed resource wrapper для Strings.resx (ru-RU primary, en-US satellite).
// Hand-maintained. Adding new string:
//   1. Add <data name="MyKey"> в Strings.resx И Strings.en-US.resx
//   2. Add `public static string MyKey => Get();` ниже
//   3. Use in XAML: Text="{x:Static props:Strings.MyKey}"
//      где xmlns:props="clr-namespace:UiApp.Properties"
//------------------------------------------------------------------------------

#nullable enable

using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;

namespace UiApp.Properties;

public static class Strings
{
    private static readonly ResourceManager Rm = new("UiApp.Properties.Strings", typeof(Strings).Assembly);

    /// <summary>Override culture for all strings lookups (null = Thread.CurrentCulture).</summary>
    public static CultureInfo? Culture { get; set; }

    private static string Get([CallerMemberName] string? name = null)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        return Rm.GetString(name, Culture) ?? name;
    }

    // Menu + tabs
    public static string MenuButton => Get();
    public static string MenuSettings => Get();
    public static string MenuClearRecent => Get();
    public static string MenuExit => Get();
    public static string HelpTooltip => Get();
    public static string ThemeToggleTooltip => Get();
    public static string TabCreateSession => Get();
    public static string TabJoin => Get();
    public static string TabAddressBook => Get();

    // Main window common
    public static string LabelLogin => Get();
    public static string LabelPassword => Get();
    public static string CreateSessionButton => Get();
    public static string RefreshPasswordButton => Get();
    public static string RecentConnected => Get();
    public static string ToAddressBookLink => Get();
    public static string JoinIntro => Get();
    public static string JoinButton => Get();
    public static string AddContactTitle => Get();
    public static string LabelName => Get();
    public static string LabelCodes => Get();
    public static string AddButton => Get();
    public static string EmptyContacts => Get();
    public static string EmptyContactsHint => Get();
    public static string Join_LoginPlaceholder => Get();
    public static string Join_PasswordPlaceholder => Get();
    public static string AddressBook_Contacts => Get();
    public static string AddressBook_Edit_Tooltip => Get();
    public static string AddressBook_ContactName_Placeholder => Get();
    public static string Tooltip_CopyLoginPassword => Get();
    public static string Tooltip_SaveToAddressBook => Get();

    // Update badge
    public static string Update_Badge_Label => Get();
    public static string Update_Tooltip_NewVersion => Get();
    public static string Update_Tooltip_Available => Get();
    public static string Update_Tooltip_ClickToOpen => Get();

    // Lockout banner
    public static string LockoutBanner_OpenSettings_Tooltip => Get();

    // Toast
    public static string Toast_ViewerConnected => Get();

    // Onboarding
    public static string Onboarding_Welcome_Title => Get();
    public static string Onboarding_Welcome_Text => Get();

    // Dialogs
    public static string Dialog_Title_Confirm => Get();
    public static string Dialog_Title_SaveConnection => Get();
    public static string Dialog_Title_UnattendedPassword => Get();
    public static string Dialog_Title_SetPassword => Get();
    public static string Dialog_PromptTitle => Get();
    public static string Dialog_PromptMessage => Get();
    public static string Dialog_Password_Hint => Get();
    public static string Dialog_Password_Hint2 => Get();
    public static string Dialog_Password_MinLength => Get();
    public static string Dialog_Password_ToggleTooltip => Get();
    public static string Dialog_SetPassword_Hint => Get();
    public static string Dialog_SetPassword_Heading => Get();
    public static string Dialog_SetPassword_NewPassword => Get();
    public static string Dialog_SetPassword_ConfirmLabel => Get();
    public static string Dialog_SetPassword_ShowButton => Get();
    public static string Dialog_SetPassword_HideButton => Get();
    public static string Dialog_SetPassword_Toggle_Tooltip => Get();
    public static string Dialog_RememberPassword_Checkbox => Get();
    public static string Dialog_RememberPassword_Tooltip => Get();

    // Common buttons
    public static string Button_Cancel => Get();
    public static string Button_Later => Get();
    public static string Button_Save => Get();
    public static string Button_Connect => Get();
    public static string Button_Delete => Get();
    public static string Button_Apply => Get();
    public static string Button_Skip => Get();
    public static string Button_Next => Get();
    public static string Button_RequestPermission => Get();
    public static string Button_CheckUpdateNow => Get();
    public static string Button_TestConnection => Get();
    public static string Button_TestConnection_Tooltip => Get();
    public static string Button_OpenLogFile => Get();
    public static string Button_OpenLogFile_Tooltip => Get();
    public static string Button_ClearLogFiles => Get();
    public static string Button_ClearLogFiles_Tooltip => Get();
    public static string Button_TaskManager => Get();
    public static string Button_TaskManager_Tooltip => Get();
    public static string Button_Reconnect => Get();
    public static string Button_Reconnect_Tooltip => Get();
    public static string Button_FileTransfer => Get();

    // Settings
    public static string SettingsWindowTitle => Get();
    public static string SettingsLanguageLabel => Get();
    public static string SettingsLanguageHint => Get();
    public static string Settings_Language_Tooltip => Get();
    public static string LanguageRussian => Get();
    public static string LanguageEnglish => Get();
    public static string Settings_Tab_Application => Get();
    public static string Settings_Tab_Server => Get();
    public static string Settings_Tab_Logs => Get();
    public static string Settings_Tab_About => Get();
    public static string Settings_Session_Label => Get();
    public static string Settings_Updates_Label => Get();
    public static string Settings_Security_Label => Get();
    public static string Settings_Unattended_Card_Title => Get();
    public static string Settings_AutoCreate => Get();
    public static string Settings_AutoCreate_Tooltip => Get();
    public static string Settings_AutoUpdate => Get();
    public static string Settings_AutoUpdate_Tooltip => Get();
    public static string Settings_Unattended => Get();
    public static string Settings_Unattended_Tooltip => Get();
    public static string Settings_Unattended_Status_NotRequired => Get();
    public static string Settings_Unattended_Status_Set => Get();
    public static string Settings_SetPassword => Get();
    public static string Settings_ChangePassword => Get();
    public static string Settings_LockoutActive => Get();
    public static string Settings_ResetLockout => Get();
    public static string Settings_ResetLockout_Tooltip => Get();
    public static string Settings_FileTransferOnly => Get();
    public static string Settings_FileTransferOnly_Tooltip => Get();
    public static string Settings_SessionDuration => Get();
    public static string Settings_SessionDuration_Tooltip => Get();
    public static string Settings_Option_5min => Get();
    public static string Settings_Option_30min => Get();
    public static string Settings_Option_1hour => Get();
    public static string Settings_Option_24hour => Get();
    public static string Settings_Option_7days => Get();
    public static string Settings_ID_Label => Get();
    public static string Settings_ID_Tooltip => Get();
    public static string Settings_ServerAddresses => Get();
    public static string Settings_ServerAPI_Tooltip => Get();
    public static string Settings_WebSocket_Tooltip => Get();
    public static string Settings_STUN_TURN_Label => Get();
    public static string Settings_STUN_Tooltip => Get();
    public static string Settings_TURN_Tooltip => Get();
    public static string Settings_TURN_Username_Tooltip => Get();
    public static string Settings_TURN_Password_Tooltip => Get();
    // TURN status panel (Variant C UI redesign 2026-04-25)
    public static string Settings_TURN_Status_Active => Get();
    public static string Settings_TURN_Status_Static => Get();
    public static string Settings_TURN_Status_None => Get();
    public static string Settings_TURN_Live_Url_Label => Get();
    public static string Settings_TURN_Live_Username_Label => Get();
    public static string Settings_TURN_Expires_Label => Get();
    public static string Settings_TURN_Expires_Format => Get();
    public static string Settings_TURN_Expires_Soon_Format => Get();
    public static string Settings_TURN_Custom_Header => Get();
    public static string Settings_TURN_Custom_Hint => Get();
    public static string Settings_Diagnostics_Label => Get();
    public static string Settings_ForceRelay => Get();
    public static string Settings_ForceRelay_Tooltip => Get();
    public static string Settings_DisableTURN => Get();
    public static string Settings_DisableTURN_Tooltip => Get();
    public static string Settings_AutoConnection_Hint => Get();
    public static string Settings_Debug_Filters_Label => Get();
    public static string Settings_Debug_Filters_Hint => Get();
    public static string Settings_DebugFilter_DCInput_Tooltip => Get();
    public static string Settings_DebugFilter_Clipboard_Tooltip => Get();
    public static string Settings_DebugFilter_Signaling_Tooltip => Get();
    public static string Settings_DebugFilter_WebRTC_Tooltip => Get();
    public static string Settings_LogFiles_Label => Get();
    public static string Settings_ICE_Debug_Label => Get();
    public static string Settings_ICE_Debug_Hint => Get();
    public static string Settings_ICE_Debug_Tooltip => Get();
    public static string Settings_About_Intro => Get();
    public static string Settings_About_Intro_Line1 => Get();
    public static string Settings_About_Intro_Line2 => Get();
    public static string Settings_About_Intro_Line3 => Get();
    public static string Settings_About_Platform => Get();
    public static string Settings_About_Capture => Get();
    public static string Settings_About_Transport => Get();
    public static string Settings_About_Server => Get();
    public static string Settings_About_Website => Get();
    public static string Settings_About_Copyright => Get();
    public static string Settings_About_PrivacyPolicy => Get();
    public static string Settings_About_MadeWith => Get();

    // Remote screen window
    public static string RemoteScreen_Title => Get();
    public static string RemoteScreen_Quality_Label => Get();
    public static string RemoteScreen_Display_Label => Get();
    public static string RemoteScreen_WaitingForVideo => Get();
    public static string RemoteScreen_FileTransfer_Tooltip => Get();

    // File transfer
    public static string FileTransfer_Title => Get();
    public static string FileTransfer_LocalPanel => Get();
    public static string FileTransfer_Tooltip_Home => Get();
    public static string FileTransfer_Tooltip_Up => Get();
    public static string FileTransfer_Tooltip_Refresh => Get();
    public static string FileTransfer_NewFolder => Get();
    public static string FileTransfer_Button_Folder => Get();
    public static string FileTransfer_Tooltip_Sort => Get();
    public static string FileTransfer_Sort_Name => Get();
    public static string FileTransfer_Sort_Size => Get();
    public static string FileTransfer_Sort_Type => Get();
    public static string FileTransfer_View_Details => Get();
    public static string FileTransfer_View_Small => Get();
    public static string FileTransfer_View_Large => Get();
    public static string FileTransfer_ContextMenu_Properties => Get();
    public static string FileTransfer_ContextMenu_Rename => Get();
    public static string FileTransfer_ContextMenu_CopyPath => Get();
    public static string FileTransfer_ContextMenu_Delete => Get();
    public static string FileTransfer_Button_Pause => Get();
    public static string FileTransfer_Button_Resume => Get();
    public static string FileTransfer_Button_CancelAll => Get();
    public static string FileTransfer_Status_NoTransfers => Get();
    public static string FileTransfer_Status_Loading => Get();

    // Toasts / Tray / Theme
    public static string Toast_MinimizedToTray => Get();
    public static string Toast_ViewerConnectedToSession => Get();
    public static string Tray_Open => Get();
    public static string Tray_Exit => Get();
    public static string Theme_Light_Tooltip => Get();
    public static string Theme_Dark_Tooltip => Get();

    // Help dialog
    public static string Help_Title => Get();
    public static string Help_Shortcut_ToggleTheme => Get();
    public static string Help_Shortcut_OpenSettings => Get();
    public static string Help_Shortcut_MainTabs => Get();
    public static string Help_Shortcut_CloseDialog => Get();
    public static string Help_NotYetMessage => Get();
    public static string Help_AboutTitle => Get();

    // Logs
    public static string Logs_NotFound => Get();
    public static string Logs_OpenError_Format => Get();
    public static string Logs_Category => Get();

    // Session copy
    public static string Session_Copy_LoginLabel => Get();
    public static string Session_Copy_PasswordLabel => Get();
    public static string Session_NotCreatedYet => Get();
    public static string Session_CopyError => Get();

    // Update check
    public static string UpdateCheck_ServerReturned_Format => Get();
    public static string UpdateCheck_EmptyResponse => Get();
    public static string UpdateCheck_UpToDate_Format => Get();
    public static string UpdateCheck_Available_Format => Get();
    public static string UpdateCheck_InvalidVersion_Format => Get();
    public static string UpdateCheck_Error_Format => Get();

    // Password dialogs
    public static string Password_Strength_Weak => Get();
    public static string Password_Strength_Medium => Get();
    public static string Password_Strength_Good => Get();
    public static string Password_Strength_Strong => Get();
    public static string Password_Mismatch => Get();
    public static string Password_MinLength_Format => Get();
    public static string UnattendedPassword_Incorrect => Get();
    public static string UnattendedPassword_IncorrectWithAttempts_Format => Get();
    public static string UnattendedPassword_RememberForContact_Format => Get();
    public static string UnattendedPassword_LastAttempt => Get();
    public static string UnattendedPassword_LockoutLifted => Get();
    public static string UnattendedPassword_LockoutCountdown_Format => Get();
    public static string UnattendedPassword_TierHint_Format => Get();
    public static string UnattendedPassword_LengthOk => Get();
    public static string Password_Valid => Get();

    // Onboarding
    public static string Onboarding_Step1_Text_Full => Get();
    public static string Onboarding_Step2_Title => Get();
    public static string Onboarding_Step2_Text => Get();
    public static string Onboarding_Step3_Title => Get();
    public static string Onboarding_Step3_Text => Get();
    public static string Onboarding_Step4_Title => Get();
    public static string Onboarding_Step4_Text => Get();
    public static string Onboarding_Privacy_Open_Link => Get();
    public static string Onboarding_Privacy_Ack => Get();

    // Menu / dialogs extra
    public static string Menu_ClearRecent_Confirm => Get();
    public static string Settings_ClearLogs_Confirm => Get();
    public static string AddressBook_Remove_Confirm_Format => Get();
    public static string Reconnect_Attempt_Format => Get();
    public static string About_Build_Format => Get();
    public static string Time_JustNow => Get();
    public static string Time_Yesterday => Get();
    public static string Time_MinutesAgo_Format => Get();
    public static string Time_HoursAgo_Format => Get();
    public static string Time_DaysAgo_Format => Get();
    public static string Time_WeeksAgo_Format => Get();
    public static string Time_MonthsAgo_Format => Get();
    public static string Presence_Online_Tooltip => Get();
    public static string Presence_Offline_Tooltip => Get();
    public static string Presence_Unknown_Tooltip => Get();

    // Extra common buttons
    public static string Button_Close => Get();
    public static string Button_Done => Get();
    public static string Button_Clear => Get();
    public static string Error_Title => Get();

    // Status messages (code)
    public static string Status_SessionAlreadyExists => Get();
    public static string Status_PasswordRefreshed => Get();
    public static string Status_NoActiveSession => Get();
    public static string Status_WaitingForService => Get();
    public static string Status_PasswordRefreshError => Get();
    public static string Status_RemoteScreenOpened => Get();
    public static string Status_InvalidCredentials => Get();
    public static string Status_ViewerConnected => Get();
    public static string Status_ViewerDisconnected => Get();
    public static string Status_ContactNameEmpty => Get();
    public static string Status_InvalidContactCode => Get();
    public static string Status_ContactAdded => Get();
    public static string Status_ContactModified => Get();
    public static string Status_EditingContact => Get();
    public static string Status_ContactDeleted => Get();
    public static string Status_SelectContact => Get();
    public static string Status_CreatingSession => Get();
    public static string Status_SessionCreated => Get();
    public static string Status_CreateSessionError => Get();
    public static string Status_Connecting => Get();
    public static string Status_ConnectError => Get();
    public static string Status_CopiedLoginPassword => Get();
    public static string Status_SessionExpired => Get();
    public static string Status_SessionExpired_NewRequired => Get();
    public static string Status_Checking => Get();
    public static string Status_APIok => Get();

    // Ban / Block / Lock server-side reject notifications
    public static string Status_Banned => Get();
    public static string Status_Maintenance => Get();
    public static string Status_SessionBlocked => Get();
    public static string Status_SessionLocked_Format => Get();
    public static string Status_SessionLocked_NoTime => Get();
    public static string Status_NetworkError => Get();
    public static string Status_ServerError => Get();
    public static string Dialog_Title_Blocked => Get();
    public static string Dialog_Title_Maintenance => Get();
    public static string Dialog_ViewerApproval_Message => Get();
    public static string Dialog_ViewerApproval_ConfirmButton => Get();
    public static string Settings_ClearUnattendedPassword_Confirm => Get();
    public static string EditContactTitle => Get();
    public static string Status_ViewerDisconnectedReconnecting => Get();
    public static string Status_ConnectionLostReconnecting => Get();
    public static string Status_ConnectionLostShort => Get();
    public static string Session_Countdown_HoursFormat => Get();
    public static string Session_Countdown_MinSecFormat => Get();
    public static string HostStats_Format => Get();
    public static string SaveToAddressBook_Title => Get();
    public static string SaveToAddressBook_Message => Get();
    public static string Status_ContactSavedFromHistory_Format => Get();
}
