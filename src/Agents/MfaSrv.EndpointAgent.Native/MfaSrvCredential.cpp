// MfaSrv Credential Implementation
// ICredentialProviderCredential and IConnectableCredentialProviderCredential.
// Handles logon UI fields, credential serialization, and MFA via named pipe.

#include "CredentialProvider.h"
#include "NamedPipeClient.h"
#include <shlwapi.h>
#include <strsafe.h>
#include <ntsecapi.h>
#define SECURITY_WIN32
#include <security.h>
#include <string.h>

#pragma comment(lib, "shlwapi.lib")
#pragma comment(lib, "secur32.lib")

// ---------------------------------------------------------------------------
// Helper: Wide to UTF-8 (stack buffer, no allocation)
// ---------------------------------------------------------------------------
static int WideToUtf8(LPCWSTR pwsz, char* pszOut, int cbOut)
{
    if (!pwsz || !pszOut || cbOut <= 0)
        return 0;
    int cb = WideCharToMultiByte(CP_UTF8, 0, pwsz, -1, pszOut, cbOut, NULL, NULL);
    if (cb == 0)
        pszOut[0] = '\0';
    return cb;
}


// ===========================================================================
// MfaSrvCredential
// ===========================================================================

MfaSrvCredential::MfaSrvCredential()
    : _cRef(1)
    , _cpus(CPUS_INVALID)
    , _pcpce(NULL)
    , _bMfaRequired(FALSE)
    , _bMfaCompleted(FALSE)
{
    _wszLargeText[0] = L'\0';
    _wszUsername[0] = L'\0';
    _wszPassword[0] = L'\0';
    _wszOtp[0] = L'\0';
    _szChallengeId[0] = '\0';
}

MfaSrvCredential::~MfaSrvCredential()
{
    // Securely clear password and OTP from memory
    SecureZeroMemory(_wszPassword, sizeof(_wszPassword));
    SecureZeroMemory(_wszOtp, sizeof(_wszOtp));
    SecureZeroMemory(_szChallengeId, sizeof(_szChallengeId));

    if (_pcpce)
    {
        _pcpce->Release();
        _pcpce = NULL;
    }
}

HRESULT MfaSrvCredential::Initialize(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus)
{
    __try
    {
        _cpus = cpus;
        StringCchCopyW(_wszLargeText, ARRAYSIZE(_wszLargeText), L"MfaSrv MFA");
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ---------------------------------------------------------------------------
// IUnknown
// ---------------------------------------------------------------------------

IFACEMETHODIMP_(ULONG) MfaSrvCredential::AddRef()
{
    return InterlockedIncrement(&_cRef);
}

IFACEMETHODIMP_(ULONG) MfaSrvCredential::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0)
        delete this;
    return cRef;
}

IFACEMETHODIMP MfaSrvCredential::QueryInterface(REFIID riid, void** ppv)
{
    __try
    {
        if (!ppv)
            return E_INVALIDARG;

        *ppv = NULL;

        if (IsEqualIID(riid, IID_IUnknown))
        {
            *ppv = static_cast<ICredentialProviderCredential*>(this);
        }
        else if (IsEqualIID(riid, IID_ICredentialProviderCredential))
        {
            *ppv = static_cast<ICredentialProviderCredential*>(this);
        }
        else if (IsEqualIID(riid, IID_IConnectableCredentialProviderCredential))
        {
            *ppv = static_cast<IConnectableCredentialProviderCredential*>(this);
        }
        else
        {
            return E_NOINTERFACE;
        }

        AddRef();
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ---------------------------------------------------------------------------
// ICredentialProviderCredential
// ---------------------------------------------------------------------------

IFACEMETHODIMP MfaSrvCredential::Advise(ICredentialProviderCredentialEvents* pcpce)
{
    __try
    {
        if (_pcpce)
            _pcpce->Release();

        _pcpce = pcpce;
        if (_pcpce)
            _pcpce->AddRef();

        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::UnAdvise()
{
    __try
    {
        if (_pcpce)
        {
            _pcpce->Release();
            _pcpce = NULL;
        }
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::SetSelected(BOOL* pbAutoLogon)
{
    __try
    {
        if (pbAutoLogon)
            *pbAutoLogon = FALSE;
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::SetDeselected()
{
    __try
    {
        // Clear sensitive fields when tile is deselected
        SecureZeroMemory(_wszPassword, sizeof(_wszPassword));
        SecureZeroMemory(_wszOtp, sizeof(_wszOtp));

        if (_pcpce)
        {
            _pcpce->SetFieldString(this, MFASRV_FID_PASSWORD, L"");
            _pcpce->SetFieldString(this, MFASRV_FID_OTP, L"");
        }

        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::GetFieldState(
    DWORD dwFieldID,
    CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
    CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis)
{
    __try
    {
        if (dwFieldID >= MFASRV_FID_COUNT)
            return E_INVALIDARG;

        if (pcpfs)
        {
            *pcpfs = s_rgFieldDescs[dwFieldID].cpfs;

            // If MFA is required and not yet completed, show the OTP field
            if (dwFieldID == MFASRV_FID_OTP && _bMfaRequired && !_bMfaCompleted)
            {
                *pcpfs = CPFS_DISPLAY_IN_SELECTED_TILE;
            }
        }

        if (pcpfis)
        {
            *pcpfis = s_rgFieldDescs[dwFieldID].cpfis;

            // Focus the OTP field when MFA is required
            if (dwFieldID == MFASRV_FID_OTP && _bMfaRequired && !_bMfaCompleted)
            {
                *pcpfis = CPFIS_FOCUSED;
            }
        }

        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::GetStringValue(DWORD dwFieldID, LPWSTR* ppwsz)
{
    __try
    {
        if (!ppwsz)
            return E_INVALIDARG;

        *ppwsz = NULL;

        HRESULT hr = E_INVALIDARG;

        switch (dwFieldID)
        {
        case MFASRV_FID_LARGE_TEXT:
            hr = SHStrDupW(_wszLargeText, ppwsz);
            break;
        case MFASRV_FID_USERNAME:
            hr = SHStrDupW(_wszUsername, ppwsz);
            break;
        case MFASRV_FID_PASSWORD:
            hr = SHStrDupW(_wszPassword, ppwsz);
            break;
        case MFASRV_FID_OTP:
            hr = SHStrDupW(_wszOtp, ppwsz);
            break;
        case MFASRV_FID_SUBMIT:
            hr = SHStrDupW(L"", ppwsz);
            break;
        default:
            break;
        }

        return hr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::GetBitmapValue(DWORD dwFieldID, HBITMAP* phbmp)
{
    __try
    {
        UNREFERENCED_PARAMETER(dwFieldID);
        if (phbmp)
            *phbmp = NULL;
        return E_NOTIMPL;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::GetCheckboxValue(
    DWORD dwFieldID, BOOL* pbChecked, LPWSTR* ppwszLabel)
{
    __try
    {
        UNREFERENCED_PARAMETER(dwFieldID);
        UNREFERENCED_PARAMETER(pbChecked);
        UNREFERENCED_PARAMETER(ppwszLabel);
        return E_NOTIMPL;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::GetComboBoxValueCount(
    DWORD dwFieldID, DWORD* pcItems, DWORD* pdwSelectedItem)
{
    __try
    {
        UNREFERENCED_PARAMETER(dwFieldID);
        UNREFERENCED_PARAMETER(pcItems);
        UNREFERENCED_PARAMETER(pdwSelectedItem);
        return E_NOTIMPL;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::GetComboBoxValueAt(
    DWORD dwFieldID, DWORD dwItem, LPWSTR* ppwszItem)
{
    __try
    {
        UNREFERENCED_PARAMETER(dwFieldID);
        UNREFERENCED_PARAMETER(dwItem);
        UNREFERENCED_PARAMETER(ppwszItem);
        return E_NOTIMPL;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::SetStringValue(DWORD dwFieldID, LPCWSTR pwz)
{
    __try
    {
        if (!pwz)
            return E_INVALIDARG;

        HRESULT hr = S_OK;

        switch (dwFieldID)
        {
        case MFASRV_FID_USERNAME:
            hr = StringCchCopyW(_wszUsername, ARRAYSIZE(_wszUsername), pwz);
            break;
        case MFASRV_FID_PASSWORD:
            hr = StringCchCopyW(_wszPassword, ARRAYSIZE(_wszPassword), pwz);
            break;
        case MFASRV_FID_OTP:
            hr = StringCchCopyW(_wszOtp, ARRAYSIZE(_wszOtp), pwz);
            break;
        default:
            hr = E_INVALIDARG;
            break;
        }

        return hr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::SetCheckboxValue(DWORD dwFieldID, BOOL bChecked)
{
    __try
    {
        UNREFERENCED_PARAMETER(dwFieldID);
        UNREFERENCED_PARAMETER(bChecked);
        return E_NOTIMPL;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::SetComboBoxSelectedValue(DWORD dwFieldID, DWORD dwSelectedItem)
{
    __try
    {
        UNREFERENCED_PARAMETER(dwFieldID);
        UNREFERENCED_PARAMETER(dwSelectedItem);
        return E_NOTIMPL;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::CommandLinkClicked(DWORD dwFieldID)
{
    __try
    {
        UNREFERENCED_PARAMETER(dwFieldID);
        return E_NOTIMPL;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::GetSubmitButtonValue(DWORD dwFieldID, DWORD* pdwAdjacentTo)
{
    __try
    {
        if (dwFieldID != MFASRV_FID_SUBMIT || !pdwAdjacentTo)
            return E_INVALIDARG;

        // Place submit button adjacent to OTP field if MFA is active,
        // otherwise adjacent to password field
        if (_bMfaRequired && !_bMfaCompleted)
            *pdwAdjacentTo = MFASRV_FID_OTP;
        else
            *pdwAdjacentTo = MFASRV_FID_PASSWORD;

        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ---------------------------------------------------------------------------
// GetSerialization - Called when the user clicks "Sign in"
// Packages credentials for Windows logon and performs MFA check.
// ---------------------------------------------------------------------------
IFACEMETHODIMP MfaSrvCredential::GetSerialization(
    CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
    LPWSTR* ppwszOptionalStatusText,
    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon)
{
    __try
    {
        if (!pcpgsr || !pcpcs || !ppwszOptionalStatusText || !pcpsiOptionalStatusIcon)
            return E_INVALIDARG;

        *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
        *ppwszOptionalStatusText = NULL;
        *pcpsiOptionalStatusIcon = CPSI_NONE;
        ZeroMemory(pcpcs, sizeof(*pcpcs));

        // Validate inputs
        if (_wszUsername[0] == L'\0')
        {
            SHStrDupW(L"Please enter a username.", ppwszOptionalStatusText);
            *pcpsiOptionalStatusIcon = CPSI_ERROR;
            return S_OK;
        }

        if (_wszPassword[0] == L'\0')
        {
            SHStrDupW(L"Please enter a password.", ppwszOptionalStatusText);
            *pcpsiOptionalStatusIcon = CPSI_ERROR;
            return S_OK;
        }

        // Perform MFA check via named pipe to Endpoint Agent
        HRESULT hrMfa = _PerformMfaCheck();

        if (hrMfa == HRESULT_FROM_WIN32(ERROR_PIPE_NOT_CONNECTED) || hrMfa == E_FAIL)
        {
            // Fail-open: Endpoint Agent not available, proceed with Windows logon
            // This is by design - if the agent is down, don't block logon
        }
        else if (FAILED(hrMfa))
        {
            // MFA explicitly denied
            SHStrDupW(L"MFA verification failed. Access denied.", ppwszOptionalStatusText);
            *pcpsiOptionalStatusIcon = CPSI_ERROR;
            *pcpgsr = CPGSR_NO_CREDENTIAL_FINISHED;
            return S_OK;
        }

        // If MFA required but OTP not yet provided, request it
        if (_bMfaRequired && !_bMfaCompleted && _wszOtp[0] == L'\0')
        {
            // Show the OTP field
            if (_pcpce)
            {
                _pcpce->SetFieldState(this, MFASRV_FID_OTP, CPFS_DISPLAY_IN_SELECTED_TILE);
                _pcpce->SetFieldInteractiveState(this, MFASRV_FID_OTP, CPFIS_FOCUSED);
            }

            SHStrDupW(L"MFA required. Please enter your OTP code.", ppwszOptionalStatusText);
            *pcpsiOptionalStatusIcon = CPSI_WARNING;
            *pcpgsr = CPGSR_NO_CREDENTIAL_NOT_FINISHED;
            return S_OK;
        }

        // Pack the credential for Windows logon
        HRESULT hr = _PackCredentialSerialization(pcpcs);
        if (FAILED(hr))
        {
            SHStrDupW(L"Internal error packaging credentials.", ppwszOptionalStatusText);
            *pcpsiOptionalStatusIcon = CPSI_ERROR;
            return S_OK;
        }

        *pcpgsr = CPGSR_RETURN_CREDENTIAL_FINISHED;
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredential::ReportResult(
    NTSTATUS ntsStatus, NTSTATUS ntsSubstatus,
    LPWSTR* ppwszOptionalStatusText,
    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon)
{
    __try
    {
        UNREFERENCED_PARAMETER(ntsStatus);
        UNREFERENCED_PARAMETER(ntsSubstatus);

        if (ppwszOptionalStatusText)
            *ppwszOptionalStatusText = NULL;
        if (pcpsiOptionalStatusIcon)
            *pcpsiOptionalStatusIcon = CPSI_NONE;

        // Reset MFA state for next attempt
        _bMfaRequired = FALSE;
        _bMfaCompleted = FALSE;
        SecureZeroMemory(_szChallengeId, sizeof(_szChallengeId));
        SecureZeroMemory(_wszOtp, sizeof(_wszOtp));

        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ---------------------------------------------------------------------------
// IConnectableCredentialProviderCredential
// ---------------------------------------------------------------------------

IFACEMETHODIMP MfaSrvCredential::Connect(IQueryContinueWithStatus* pqcws)
{
    __try
    {
        // The Connect method is called for connectable credentials.
        // We use it as a secondary MFA verification pathway.

        if (pqcws)
        {
            pqcws->SetStatusMessage(L"Verifying MFA with MfaSrv...");
        }

        HRESULT hr = _PerformMfaCheck();

        if (SUCCEEDED(hr) || hr == E_FAIL)
        {
            // S_OK = MFA passed, E_FAIL = fail-open (agent unavailable)
            return S_OK;
        }

        // MFA denied
        return E_ACCESSDENIED;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        // Fail-open on unexpected exception
        return S_OK;
    }
}

IFACEMETHODIMP MfaSrvCredential::Disconnect()
{
    __try
    {
        return E_NOTIMPL;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ---------------------------------------------------------------------------
// _PerformMfaCheck - Communicate with Endpoint Agent via named pipe
// ---------------------------------------------------------------------------
HRESULT MfaSrvCredential::_PerformMfaCheck()
{
    __try
    {
        HANDLE hPipe = INVALID_HANDLE_VALUE;
        HRESULT hr = MfaPipeConnect(&hPipe);

        if (FAILED(hr) || hPipe == INVALID_HANDLE_VALUE)
        {
            // Cannot reach agent - fail open
            return E_FAIL;
        }

        // Get computer name for workstation field
        char szWorkstation[MAX_COMPUTERNAME_LENGTH + 1] = { 0 };
        WCHAR wszWorkstation[MAX_COMPUTERNAME_LENGTH + 1] = { 0 };
        DWORD cchWorkstation = MAX_COMPUTERNAME_LENGTH + 1;
        GetComputerNameW(wszWorkstation, &cchWorkstation);
        WideToUtf8(wszWorkstation, szWorkstation, sizeof(szWorkstation));

        // Convert username to UTF-8
        char szUsername[512] = { 0 };
        WideToUtf8(_wszUsername, szUsername, sizeof(szUsername));

        // Parse domain\user if present
        char szDomain[256] = { 0 };
        char szUser[256] = { 0 };
        {
            char* pBackslash = NULL;
            for (char* p = szUsername; *p; p++)
            {
                if (*p == '\\')
                {
                    pBackslash = p;
                    break;
                }
            }
            if (pBackslash)
            {
                int domLen = (int)(pBackslash - szUsername);
                if (domLen > 0 && domLen < (int)sizeof(szDomain))
                {
                    for (int i = 0; i < domLen; i++)
                        szDomain[i] = szUsername[i];
                    szDomain[domLen] = '\0';
                }
                StringCchCopyA(szUser, ARRAYSIZE(szUser), pBackslash + 1);
            }
            else
            {
                StringCchCopyA(szUser, ARRAYSIZE(szUser), szUsername);
                szDomain[0] = '.';
                szDomain[1] = '\0';
            }
        }

        // Build PreAuth JSON message
        char szJson[2048] = { 0 };
        int pos = 0;
        JsonAppendRaw(szJson, sizeof(szJson), &pos, "{\"type\":\"preauth\",\"userName\":\"");
        JsonAppendEscaped(szJson, sizeof(szJson), &pos, szUser);
        JsonAppendRaw(szJson, sizeof(szJson), &pos, "\",\"domain\":\"");
        JsonAppendEscaped(szJson, sizeof(szJson), &pos, szDomain);
        JsonAppendRaw(szJson, sizeof(szJson), &pos, "\",\"workstation\":\"");
        JsonAppendEscaped(szJson, sizeof(szJson), &pos, szWorkstation);
        JsonAppendRaw(szJson, sizeof(szJson), &pos, "\"}");

        hr = MfaPipeSend(hPipe, szJson, (DWORD)pos);
        if (FAILED(hr))
        {
            MfaPipeClose(hPipe);
            return E_FAIL; // Fail-open
        }

        // Read PreAuth response
        char szResponse[4096] = { 0 };
        DWORD cbRead = 0;
        hr = MfaPipeRead(hPipe, szResponse, sizeof(szResponse), &cbRead);
        if (FAILED(hr))
        {
            MfaPipeClose(hPipe);
            return E_FAIL; // Fail-open
        }

        // Parse response: check if MFA is required
        char szStatus[64] = { 0 };
        JsonGetString(szResponse, "status", szStatus, sizeof(szStatus));

        if (szStatus[0] == '\0')
        {
            // No valid response - fail open
            MfaPipeClose(hPipe);
            return E_FAIL;
        }

        // Check status: "approved" = no MFA needed, "mfa_required" = need OTP
        if (szStatus[0] == 'a') // "approved"
        {
            MfaPipeClose(hPipe);
            _bMfaRequired = FALSE;
            _bMfaCompleted = TRUE;
            return S_OK;
        }

        if (szStatus[0] == 'd') // "denied"
        {
            MfaPipeClose(hPipe);
            return E_ACCESSDENIED;
        }

        if (szStatus[0] == 'm') // "mfa_required"
        {
            // Extract challenge ID
            JsonGetString(szResponse, "challengeId", _szChallengeId, sizeof(_szChallengeId));
            _bMfaRequired = TRUE;
            _bMfaCompleted = FALSE;

            // If we already have an OTP, submit it now
            if (_wszOtp[0] != L'\0')
            {
                char szOtp[128] = { 0 };
                WideToUtf8(_wszOtp, szOtp, sizeof(szOtp));

                // Build submit_mfa JSON
                pos = 0;
                ZeroMemory(szJson, sizeof(szJson));
                JsonAppendRaw(szJson, sizeof(szJson), &pos, "{\"type\":\"submit_mfa\",\"challengeId\":\"");
                JsonAppendEscaped(szJson, sizeof(szJson), &pos, _szChallengeId);
                JsonAppendRaw(szJson, sizeof(szJson), &pos, "\",\"response\":\"");
                JsonAppendEscaped(szJson, sizeof(szJson), &pos, szOtp);
                JsonAppendRaw(szJson, sizeof(szJson), &pos, "\"}");

                hr = MfaPipeSend(hPipe, szJson, (DWORD)pos);
                if (FAILED(hr))
                {
                    MfaPipeClose(hPipe);
                    return E_FAIL; // Fail-open
                }

                // Read MFA response
                ZeroMemory(szResponse, sizeof(szResponse));
                hr = MfaPipeRead(hPipe, szResponse, sizeof(szResponse), &cbRead);
                MfaPipeClose(hPipe);

                if (FAILED(hr))
                    return E_FAIL; // Fail-open

                ZeroMemory(szStatus, sizeof(szStatus));
                JsonGetString(szResponse, "status", szStatus, sizeof(szStatus));

                if (szStatus[0] == 'a') // "approved"
                {
                    _bMfaCompleted = TRUE;
                    return S_OK;
                }
                else if (szStatus[0] == 'd') // "denied"
                {
                    return E_ACCESSDENIED;
                }

                // Unknown status - fail open
                return E_FAIL;
            }

            // No OTP yet - caller should show OTP field
            MfaPipeClose(hPipe);
            return S_FALSE; // Indicates MFA needed but not yet provided
        }

        // Unknown status - fail open
        MfaPipeClose(hPipe);
        return E_FAIL;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        // Fail-open on any exception
        return E_FAIL;
    }
}

// ---------------------------------------------------------------------------
// _PackCredentialSerialization
// Builds a KERB_INTERACTIVE_UNLOCK_LOGON serialization for Windows to process.
// Uses the Negotiate SSP for authentication package resolution.
// ---------------------------------------------------------------------------
HRESULT MfaSrvCredential::_PackCredentialSerialization(
    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
{
    __try
    {
        if (!pcpcs)
            return E_INVALIDARG;

        ZeroMemory(pcpcs, sizeof(*pcpcs));

        // Parse domain\username
        WCHAR wszDomain[256] = { 0 };
        WCHAR wszUser[256] = { 0 };
        {
            LPCWSTR pwszBackslash = NULL;
            for (LPCWSTR p = _wszUsername; *p; p++)
            {
                if (*p == L'\\')
                {
                    pwszBackslash = p;
                    break;
                }
            }

            if (pwszBackslash)
            {
                size_t domLen = (size_t)(pwszBackslash - _wszUsername);
                if (domLen > 0 && domLen < ARRAYSIZE(wszDomain))
                {
                    for (size_t i = 0; i < domLen; i++)
                        wszDomain[i] = _wszUsername[i];
                    wszDomain[domLen] = L'\0';
                }
                StringCchCopyW(wszUser, ARRAYSIZE(wszUser), pwszBackslash + 1);
            }
            else
            {
                // No domain specified - use local computer name
                DWORD cch = ARRAYSIZE(wszDomain);
                if (!GetComputerNameW(wszDomain, &cch))
                    wszDomain[0] = L'.';
                StringCchCopyW(wszUser, ARRAYSIZE(wszUser), _wszUsername);
            }
        }

        // Calculate string lengths (in bytes, not including null terminator)
        DWORD cbDomain   = (DWORD)(wcslen(wszDomain) * sizeof(WCHAR));
        DWORD cbUser     = (DWORD)(wcslen(wszUser) * sizeof(WCHAR));
        DWORD cbPassword = (DWORD)(wcslen(_wszPassword) * sizeof(WCHAR));

        // Calculate total size: KERB_INTERACTIVE_UNLOCK_LOGON header + packed strings
        DWORD cbHeader = sizeof(KERB_INTERACTIVE_UNLOCK_LOGON);
        DWORD cbSize = cbHeader + cbDomain + cbUser + cbPassword;

        // Allocate the serialization buffer using CoTaskMemAlloc
        BYTE* pbSerialization = (BYTE*)CoTaskMemAlloc(cbSize);
        if (!pbSerialization)
            return E_OUTOFMEMORY;

        ZeroMemory(pbSerialization, cbSize);

        // Fill in the structure
        KERB_INTERACTIVE_UNLOCK_LOGON* pUnlockLogon =
            (KERB_INTERACTIVE_UNLOCK_LOGON*)pbSerialization;

        KERB_INTERACTIVE_LOGON* pLogon = &pUnlockLogon->Logon;

        pLogon->MessageType = KerbInteractiveLogon;

        // Strings are packed contiguously after the header.
        // Buffer pointers are stored as offsets from the start of the serialization.
        BYTE* pbStrings = pbSerialization + cbHeader;
        DWORD cbOffset = cbHeader;

        // Domain string
        pLogon->LogonDomainName.Length = (USHORT)cbDomain;
        pLogon->LogonDomainName.MaximumLength = (USHORT)cbDomain;
        pLogon->LogonDomainName.Buffer = (PWSTR)(ULONG_PTR)cbOffset;
        CopyMemory(pbStrings, wszDomain, cbDomain);
        pbStrings += cbDomain;
        cbOffset += cbDomain;

        // Username string
        pLogon->UserName.Length = (USHORT)cbUser;
        pLogon->UserName.MaximumLength = (USHORT)cbUser;
        pLogon->UserName.Buffer = (PWSTR)(ULONG_PTR)cbOffset;
        CopyMemory(pbStrings, wszUser, cbUser);
        pbStrings += cbUser;
        cbOffset += cbUser;

        // Password string
        pLogon->Password.Length = (USHORT)cbPassword;
        pLogon->Password.MaximumLength = (USHORT)cbPassword;
        pLogon->Password.Buffer = (PWSTR)(ULONG_PTR)cbOffset;
        CopyMemory(pbStrings, _wszPassword, cbPassword);

        // Get the Negotiate authentication package
        HANDLE hLsa = NULL;
        NTSTATUS ntStatus = LsaConnectUntrusted(&hLsa);
        if (ntStatus != STATUS_SUCCESS)
        {
            CoTaskMemFree(pbSerialization);
            return HRESULT_FROM_NT(ntStatus);
        }

        LSA_STRING lsaPackageName;
        lsaPackageName.Buffer = (PCHAR)NEGOSSP_NAME_A;
        lsaPackageName.Length = (USHORT)strlen(NEGOSSP_NAME_A);
        lsaPackageName.MaximumLength = lsaPackageName.Length + 1;

        ULONG ulAuthPackage = 0;
        ntStatus = LsaLookupAuthenticationPackage(hLsa, &lsaPackageName, &ulAuthPackage);
        LsaDeregisterLogonProcess(hLsa);

        if (ntStatus != STATUS_SUCCESS)
        {
            // Fallback: use package ID 0 (typically Negotiate/Kerberos)
            ulAuthPackage = 0;
        }

        pcpcs->ulAuthenticationPackage = ulAuthPackage;
        pcpcs->cbSerialization = cbSize;
        pcpcs->rgbSerialization = pbSerialization;
        pcpcs->clsidCredentialProvider = CLSID_MfaSrvCredentialProvider;

        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}
