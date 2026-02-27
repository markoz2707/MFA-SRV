#pragma once

// MfaSrv Credential Provider
// Windows Credential Provider DLL that adds an MFA tile to the logon screen.
// Communicates with the MfaSrv Endpoint Agent service via named pipe.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <credentialprovider.h>
#include <shlguid.h>
#include <ntsecapi.h>

// ---------------------------------------------------------------------------
// Provider GUID: {A0E9E5B0-1234-4567-89AB-CDEF01234567}
// Defined via INITGUID in CredentialProvider.cpp; declared extern elsewhere.
// ---------------------------------------------------------------------------
// {A0E9E5B0-1234-4567-89AB-CDEF01234567}
EXTERN_C const GUID CLSID_MfaSrvCredentialProvider;

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------
#define MFASRV_PIPE_NAME        L"\\\\.\\pipe\\MfaSrvEndpointAgent"
#define MFASRV_PIPE_TIMEOUT_MS  3000

// Field descriptor indices
enum MFASRV_FIELD_ID
{
    MFASRV_FID_LARGE_TEXT   = 0,    // "MfaSrv MFA" label
    MFASRV_FID_USERNAME     = 1,    // Username edit
    MFASRV_FID_PASSWORD     = 2,    // Password edit
    MFASRV_FID_OTP          = 3,    // OTP code edit
    MFASRV_FID_SUBMIT       = 4,    // Submit button
    MFASRV_FID_COUNT        = 5
};

// ---------------------------------------------------------------------------
// Field descriptor entry - shared between provider and credential
// ---------------------------------------------------------------------------
struct FIELD_DESC_ENTRY
{
    CREDENTIAL_PROVIDER_FIELD_TYPE                  cpft;
    LPCWSTR                                         pwszLabel;
    CREDENTIAL_PROVIDER_FIELD_STATE                 cpfs;
    CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE     cpfis;
    GUID                                            guidFieldType;
};

// Defined in CredentialProvider.cpp, used by MfaSrvCredential.cpp
extern const FIELD_DESC_ENTRY s_rgFieldDescs[MFASRV_FID_COUNT];

// ---------------------------------------------------------------------------
// Forward declarations
// ---------------------------------------------------------------------------
class MfaSrvCredential;

// ---------------------------------------------------------------------------
// Named pipe client (NamedPipeClient.h / .cpp)
// ---------------------------------------------------------------------------
HRESULT MfaPipeConnect(HANDLE* phPipe);
HRESULT MfaPipeSend(HANDLE hPipe, const char* pszJson, DWORD cbJson);
HRESULT MfaPipeRead(HANDLE hPipe, char* pszBuffer, DWORD cbBuffer, DWORD* pcbRead);
void    MfaPipeClose(HANDLE hPipe);

// Simple JSON value extraction (no external deps)
BOOL    JsonGetString(const char* pszJson, const char* pszKey, char* pszOut, DWORD cchOut);

// ---------------------------------------------------------------------------
// Helper: JSON builder functions (no allocation, stack-based)
// ---------------------------------------------------------------------------
void JsonAppendEscaped(char* pszBuf, int cbBuf, int* piPos, const char* pszValue);
void JsonAppendRaw(char* pszBuf, int cbBuf, int* piPos, const char* pszRaw);

// ---------------------------------------------------------------------------
// MfaSrvCredentialProvider
// Implements: ICredentialProvider, ICredentialProviderSetUserArray
// ---------------------------------------------------------------------------
class MfaSrvCredentialProvider : public ICredentialProvider,
                                 public ICredentialProviderSetUserArray
{
public:
    MfaSrvCredentialProvider();

    // IUnknown
    IFACEMETHODIMP_(ULONG) AddRef();
    IFACEMETHODIMP_(ULONG) Release();
    IFACEMETHODIMP         QueryInterface(REFIID riid, void** ppv);

    // ICredentialProvider
    IFACEMETHODIMP SetUsageScenario(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD dwFlags);
    IFACEMETHODIMP SetSerialization(const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs);
    IFACEMETHODIMP Advise(ICredentialProviderEvents* pcpe, UINT_PTR upAdviseContext);
    IFACEMETHODIMP UnAdvise();
    IFACEMETHODIMP GetFieldDescriptorCount(DWORD* pdwCount);
    IFACEMETHODIMP GetFieldDescriptorAt(DWORD dwIndex, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd);
    IFACEMETHODIMP GetCredentialCount(DWORD* pdwCount, DWORD* pdwDefault, BOOL* pbAutoLogonWithDefault);
    IFACEMETHODIMP GetCredentialAt(DWORD dwIndex, ICredentialProviderCredential** ppcpc);

    // ICredentialProviderSetUserArray
    IFACEMETHODIMP SetUserArray(ICredentialProviderUserArray* users);

private:
    ~MfaSrvCredentialProvider();

    LONG                                    _cRef;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO      _cpus;
    MfaSrvCredential*                       _pCredential;
    ICredentialProviderEvents*              _pcpe;
    UINT_PTR                                _upAdviseContext;
};

// ---------------------------------------------------------------------------
// MfaSrvCredential
// Implements: ICredentialProviderCredential, IConnectableCredentialProviderCredential
// ---------------------------------------------------------------------------
class MfaSrvCredential : public ICredentialProviderCredential,
                          public IConnectableCredentialProviderCredential
{
public:
    MfaSrvCredential();
    HRESULT Initialize(CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus);

    // IUnknown
    IFACEMETHODIMP_(ULONG) AddRef();
    IFACEMETHODIMP_(ULONG) Release();
    IFACEMETHODIMP         QueryInterface(REFIID riid, void** ppv);

    // ICredentialProviderCredential
    IFACEMETHODIMP Advise(ICredentialProviderCredentialEvents* pcpce);
    IFACEMETHODIMP UnAdvise();
    IFACEMETHODIMP SetSelected(BOOL* pbAutoLogon);
    IFACEMETHODIMP SetDeselected();
    IFACEMETHODIMP GetFieldState(DWORD dwFieldID,
                                 CREDENTIAL_PROVIDER_FIELD_STATE* pcpfs,
                                 CREDENTIAL_PROVIDER_FIELD_INTERACTIVE_STATE* pcpfis);
    IFACEMETHODIMP GetStringValue(DWORD dwFieldID, LPWSTR* ppwsz);
    IFACEMETHODIMP GetBitmapValue(DWORD dwFieldID, HBITMAP* phbmp);
    IFACEMETHODIMP GetCheckboxValue(DWORD dwFieldID, BOOL* pbChecked, LPWSTR* ppwszLabel);
    IFACEMETHODIMP GetComboBoxValueCount(DWORD dwFieldID, DWORD* pcItems, DWORD* pdwSelectedItem);
    IFACEMETHODIMP GetComboBoxValueAt(DWORD dwFieldID, DWORD dwItem, LPWSTR* ppwszItem);
    IFACEMETHODIMP SetStringValue(DWORD dwFieldID, LPCWSTR pwz);
    IFACEMETHODIMP SetCheckboxValue(DWORD dwFieldID, BOOL bChecked);
    IFACEMETHODIMP SetComboBoxSelectedValue(DWORD dwFieldID, DWORD dwSelectedItem);
    IFACEMETHODIMP CommandLinkClicked(DWORD dwFieldID);
    IFACEMETHODIMP GetSubmitButtonValue(DWORD dwFieldID, DWORD* pdwAdjacentTo);
    IFACEMETHODIMP GetSerialization(CREDENTIAL_PROVIDER_GET_SERIALIZATION_RESPONSE* pcpgsr,
                                    CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs,
                                    LPWSTR* ppwszOptionalStatusText,
                                    CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon);
    IFACEMETHODIMP ReportResult(NTSTATUS ntsStatus, NTSTATUS ntsSubstatus,
                                LPWSTR* ppwszOptionalStatusText,
                                CREDENTIAL_PROVIDER_STATUS_ICON* pcpsiOptionalStatusIcon);

    // IConnectableCredentialProviderCredential
    IFACEMETHODIMP Connect(IQueryContinueWithStatus* pqcws);
    IFACEMETHODIMP Disconnect();

private:
    ~MfaSrvCredential();

    HRESULT _PerformMfaCheck();
    HRESULT _PackCredentialSerialization(CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs);

    LONG                                    _cRef;
    CREDENTIAL_PROVIDER_USAGE_SCENARIO      _cpus;
    ICredentialProviderCredentialEvents*     _pcpce;

    // Field values
    WCHAR   _wszLargeText[64];
    WCHAR   _wszUsername[256];
    WCHAR   _wszPassword[256];
    WCHAR   _wszOtp[64];

    // MFA state
    BOOL    _bMfaRequired;
    BOOL    _bMfaCompleted;
    char    _szChallengeId[256];
};

// ---------------------------------------------------------------------------
// Class factory
// ---------------------------------------------------------------------------
class MfaSrvClassFactory : public IClassFactory
{
public:
    MfaSrvClassFactory();

    // IUnknown
    IFACEMETHODIMP_(ULONG) AddRef();
    IFACEMETHODIMP_(ULONG) Release();
    IFACEMETHODIMP         QueryInterface(REFIID riid, void** ppv);

    // IClassFactory
    IFACEMETHODIMP CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv);
    IFACEMETHODIMP LockServer(BOOL bLock);

private:
    ~MfaSrvClassFactory();
    LONG _cRef;
};

// ---------------------------------------------------------------------------
// DLL exports
// ---------------------------------------------------------------------------
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv);
STDAPI DllCanUnloadNow();
STDAPI DllRegisterServer();
STDAPI DllUnregisterServer();
