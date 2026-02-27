// MfaSrv Credential Provider - Main DLL Implementation
// Provides DLL entry points, class factory, and MfaSrvCredentialProvider.
// Safe for loading in LogonUI.exe (winlogon child process).

// INITGUID must come before any header that uses DEFINE_GUID
#include <initguid.h>

#include "CredentialProvider.h"
#include "NamedPipeClient.h"
#include <shlwapi.h>
#include <strsafe.h>
#include <new>

#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "shell32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "shlwapi.lib")

// ---------------------------------------------------------------------------
// GUID definition (INITGUID is active in this TU)
// ---------------------------------------------------------------------------
// {A0E9E5B0-1234-4567-89AB-CDEF01234567}
DEFINE_GUID(CLSID_MfaSrvCredentialProvider,
    0xa0e9e5b0, 0x1234, 0x4567,
    0x89, 0xab, 0xcd, 0xef, 0x01, 0x23, 0x45, 0x67);

// ---------------------------------------------------------------------------
// Global state
// ---------------------------------------------------------------------------
static HMODULE g_hModule = NULL;
static LONG    g_cDllRef = 0;

// Provider GUID string for registry
static const WCHAR g_wszProviderClsid[] = L"{A0E9E5B0-1234-4567-89AB-CDEF01234567}";

// ---------------------------------------------------------------------------
// Field descriptors (shared table, declared extern in header)
// ---------------------------------------------------------------------------
const FIELD_DESC_ENTRY s_rgFieldDescs[MFASRV_FID_COUNT] =
{
    // FID 0: Large text label
    { CPFT_LARGE_TEXT,      L"MfaSrv MFA",      CPFS_DISPLAY_IN_SELECTED_TILE,   CPFIS_NONE,     CPFG_CREDENTIAL_PROVIDER_LABEL },
    // FID 1: Username
    { CPFT_EDIT_TEXT,       L"Username",         CPFS_DISPLAY_IN_SELECTED_TILE,   CPFIS_NONE,     CPFG_LOGON_USERNAME },
    // FID 2: Password
    { CPFT_PASSWORD_TEXT,   L"Password",         CPFS_DISPLAY_IN_SELECTED_TILE,   CPFIS_FOCUSED,  CPFG_LOGON_PASSWORD },
    // FID 3: OTP Code (hidden until MFA required)
    { CPFT_EDIT_TEXT,       L"OTP Code",         CPFS_HIDDEN,                     CPFIS_NONE,     GUID_NULL },
    // FID 4: Submit button
    { CPFT_SUBMIT_BUTTON,  L"Sign in",          CPFS_DISPLAY_IN_SELECTED_TILE,   CPFIS_NONE,     GUID_NULL },
};

// ---------------------------------------------------------------------------
// DllMain
// ---------------------------------------------------------------------------
BOOL APIENTRY DllMain(HMODULE hModule, DWORD dwReason, LPVOID lpReserved)
{
    UNREFERENCED_PARAMETER(lpReserved);

    switch (dwReason)
    {
    case DLL_PROCESS_ATTACH:
        g_hModule = hModule;
        DisableThreadLibraryCalls(hModule);
        break;
    case DLL_PROCESS_DETACH:
        break;
    }
    return TRUE;
}

// ---------------------------------------------------------------------------
// DllCanUnloadNow
// ---------------------------------------------------------------------------
STDAPI DllCanUnloadNow()
{
    return (g_cDllRef > 0) ? S_FALSE : S_OK;
}

// ---------------------------------------------------------------------------
// DllGetClassObject
// ---------------------------------------------------------------------------
STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, void** ppv)
{
    __try
    {
        if (!ppv)
            return E_INVALIDARG;

        *ppv = NULL;

        if (!IsEqualCLSID(rclsid, CLSID_MfaSrvCredentialProvider))
            return CLASS_E_CLASSNOTAVAILABLE;

        MfaSrvClassFactory* pFactory = new(std::nothrow) MfaSrvClassFactory();
        if (!pFactory)
            return E_OUTOFMEMORY;

        HRESULT hr = pFactory->QueryInterface(riid, ppv);
        pFactory->Release();
        return hr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ---------------------------------------------------------------------------
// DllRegisterServer - Self-registration for the credential provider
// ---------------------------------------------------------------------------
STDAPI DllRegisterServer()
{
    __try
    {
        HRESULT hr = S_OK;
        HKEY hKey = NULL;
        HKEY hSubKey = NULL;
        WCHAR wszModulePath[MAX_PATH] = { 0 };

        // Get the DLL path
        if (!GetModuleFileNameW(g_hModule, wszModulePath, MAX_PATH))
            return HRESULT_FROM_WIN32(GetLastError());

        // Register COM class: HKCR\CLSID\{guid}
        WCHAR wszClsidKey[128];
        StringCchPrintfW(wszClsidKey, ARRAYSIZE(wszClsidKey),
            L"CLSID\\%s", g_wszProviderClsid);

        LONG lRes = RegCreateKeyExW(HKEY_CLASSES_ROOT, wszClsidKey, 0, NULL,
            REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &hKey, NULL);
        if (lRes != ERROR_SUCCESS)
            return HRESULT_FROM_WIN32(lRes);

        // Set default value
        static const WCHAR wszDesc[] = L"MfaSrv Credential Provider";
        RegSetValueExW(hKey, NULL, 0, REG_SZ, (const BYTE*)wszDesc, sizeof(wszDesc));

        // Create InprocServer32 subkey
        lRes = RegCreateKeyExW(hKey, L"InprocServer32", 0, NULL,
            REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &hSubKey, NULL);
        if (lRes != ERROR_SUCCESS)
        {
            RegCloseKey(hKey);
            return HRESULT_FROM_WIN32(lRes);
        }

        // Set DLL path
        DWORD cbPath = (DWORD)((wcslen(wszModulePath) + 1) * sizeof(WCHAR));
        RegSetValueExW(hSubKey, NULL, 0, REG_SZ, (const BYTE*)wszModulePath, cbPath);

        // Set threading model
        static const WCHAR wszApartment[] = L"Apartment";
        RegSetValueExW(hSubKey, L"ThreadingModel", 0, REG_SZ,
            (const BYTE*)wszApartment, sizeof(wszApartment));

        RegCloseKey(hSubKey);
        RegCloseKey(hKey);

        // Register as a credential provider
        // HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Authentication\Credential Providers\{guid}
        WCHAR wszCPKey[256];
        StringCchPrintfW(wszCPKey, ARRAYSIZE(wszCPKey),
            L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\\%s",
            g_wszProviderClsid);

        lRes = RegCreateKeyExW(HKEY_LOCAL_MACHINE, wszCPKey, 0, NULL,
            REG_OPTION_NON_VOLATILE, KEY_WRITE, NULL, &hKey, NULL);
        if (lRes != ERROR_SUCCESS)
            return HRESULT_FROM_WIN32(lRes);

        RegSetValueExW(hKey, NULL, 0, REG_SZ, (const BYTE*)wszDesc, sizeof(wszDesc));
        RegCloseKey(hKey);

        return hr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ---------------------------------------------------------------------------
// DllUnregisterServer
// ---------------------------------------------------------------------------
STDAPI DllUnregisterServer()
{
    __try
    {
        // Remove credential provider registration
        WCHAR wszCPKey[256];
        StringCchPrintfW(wszCPKey, ARRAYSIZE(wszCPKey),
            L"SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Authentication\\Credential Providers\\%s",
            g_wszProviderClsid);
        RegDeleteTreeW(HKEY_LOCAL_MACHINE, wszCPKey);

        // Remove COM registration
        WCHAR wszClsidKey[128];
        StringCchPrintfW(wszClsidKey, ARRAYSIZE(wszClsidKey),
            L"CLSID\\%s", g_wszProviderClsid);
        RegDeleteTreeW(HKEY_CLASSES_ROOT, wszClsidKey);

        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}


// ===========================================================================
// MfaSrvClassFactory
// ===========================================================================

MfaSrvClassFactory::MfaSrvClassFactory() : _cRef(1)
{
    InterlockedIncrement(&g_cDllRef);
}

MfaSrvClassFactory::~MfaSrvClassFactory()
{
    InterlockedDecrement(&g_cDllRef);
}

IFACEMETHODIMP_(ULONG) MfaSrvClassFactory::AddRef()
{
    return InterlockedIncrement(&_cRef);
}

IFACEMETHODIMP_(ULONG) MfaSrvClassFactory::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0)
        delete this;
    return cRef;
}

IFACEMETHODIMP MfaSrvClassFactory::QueryInterface(REFIID riid, void** ppv)
{
    __try
    {
        if (!ppv)
            return E_INVALIDARG;

        *ppv = NULL;

        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }

        return E_NOINTERFACE;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvClassFactory::CreateInstance(IUnknown* pUnkOuter, REFIID riid, void** ppv)
{
    __try
    {
        if (!ppv)
            return E_INVALIDARG;

        *ppv = NULL;

        if (pUnkOuter)
            return CLASS_E_NOAGGREGATION;

        MfaSrvCredentialProvider* pProvider = new(std::nothrow) MfaSrvCredentialProvider();
        if (!pProvider)
            return E_OUTOFMEMORY;

        HRESULT hr = pProvider->QueryInterface(riid, ppv);
        pProvider->Release();
        return hr;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvClassFactory::LockServer(BOOL bLock)
{
    if (bLock)
        InterlockedIncrement(&g_cDllRef);
    else
        InterlockedDecrement(&g_cDllRef);
    return S_OK;
}


// ===========================================================================
// MfaSrvCredentialProvider
// ===========================================================================

MfaSrvCredentialProvider::MfaSrvCredentialProvider()
    : _cRef(1)
    , _cpus(CPUS_INVALID)
    , _pCredential(NULL)
    , _pcpe(NULL)
    , _upAdviseContext(0)
{
    InterlockedIncrement(&g_cDllRef);
}

MfaSrvCredentialProvider::~MfaSrvCredentialProvider()
{
    if (_pCredential)
    {
        _pCredential->Release();
        _pCredential = NULL;
    }
    if (_pcpe)
    {
        _pcpe->Release();
        _pcpe = NULL;
    }
    InterlockedDecrement(&g_cDllRef);
}

IFACEMETHODIMP_(ULONG) MfaSrvCredentialProvider::AddRef()
{
    return InterlockedIncrement(&_cRef);
}

IFACEMETHODIMP_(ULONG) MfaSrvCredentialProvider::Release()
{
    LONG cRef = InterlockedDecrement(&_cRef);
    if (cRef == 0)
        delete this;
    return cRef;
}

IFACEMETHODIMP MfaSrvCredentialProvider::QueryInterface(REFIID riid, void** ppv)
{
    __try
    {
        if (!ppv)
            return E_INVALIDARG;

        *ppv = NULL;

        if (IsEqualIID(riid, IID_IUnknown))
        {
            *ppv = static_cast<ICredentialProvider*>(this);
        }
        else if (IsEqualIID(riid, IID_ICredentialProvider))
        {
            *ppv = static_cast<ICredentialProvider*>(this);
        }
        else if (IsEqualIID(riid, IID_ICredentialProviderSetUserArray))
        {
            *ppv = static_cast<ICredentialProviderSetUserArray*>(this);
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

IFACEMETHODIMP MfaSrvCredentialProvider::SetUsageScenario(
    CREDENTIAL_PROVIDER_USAGE_SCENARIO cpus, DWORD dwFlags)
{
    __try
    {
        UNREFERENCED_PARAMETER(dwFlags);

        switch (cpus)
        {
        case CPUS_LOGON:
        case CPUS_UNLOCK_WORKSTATION:
        case CPUS_CREDUI:
            _cpus = cpus;
            break;
        default:
            return E_INVALIDARG;
        }

        // Create our single credential tile
        if (!_pCredential)
        {
            _pCredential = new(std::nothrow) MfaSrvCredential();
            if (!_pCredential)
                return E_OUTOFMEMORY;

            HRESULT hr = _pCredential->Initialize(_cpus);
            if (FAILED(hr))
            {
                _pCredential->Release();
                _pCredential = NULL;
                return hr;
            }
        }

        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredentialProvider::SetSerialization(
    const CREDENTIAL_PROVIDER_CREDENTIAL_SERIALIZATION* pcpcs)
{
    __try
    {
        UNREFERENCED_PARAMETER(pcpcs);
        return E_NOTIMPL;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredentialProvider::Advise(
    ICredentialProviderEvents* pcpe, UINT_PTR upAdviseContext)
{
    __try
    {
        if (_pcpe)
            _pcpe->Release();

        _pcpe = pcpe;
        if (_pcpe)
            _pcpe->AddRef();

        _upAdviseContext = upAdviseContext;
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredentialProvider::UnAdvise()
{
    __try
    {
        if (_pcpe)
        {
            _pcpe->Release();
            _pcpe = NULL;
        }
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredentialProvider::GetFieldDescriptorCount(DWORD* pdwCount)
{
    __try
    {
        if (!pdwCount)
            return E_INVALIDARG;

        *pdwCount = MFASRV_FID_COUNT;
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredentialProvider::GetFieldDescriptorAt(
    DWORD dwIndex, CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR** ppcpfd)
{
    __try
    {
        if (!ppcpfd)
            return E_INVALIDARG;

        *ppcpfd = NULL;

        if (dwIndex >= MFASRV_FID_COUNT)
            return E_INVALIDARG;

        const FIELD_DESC_ENTRY* pEntry = &s_rgFieldDescs[dwIndex];

        // Allocate the descriptor using CoTaskMemAlloc (COM requirement)
        CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR* pfd =
            (CREDENTIAL_PROVIDER_FIELD_DESCRIPTOR*)CoTaskMemAlloc(sizeof(*pfd));
        if (!pfd)
            return E_OUTOFMEMORY;

        pfd->dwFieldID = dwIndex;
        pfd->cpft = pEntry->cpft;
        pfd->guidFieldType = pEntry->guidFieldType;

        // Duplicate the label string
        HRESULT hr = SHStrDupW(pEntry->pwszLabel, &pfd->pszLabel);
        if (FAILED(hr))
        {
            CoTaskMemFree(pfd);
            return hr;
        }

        *ppcpfd = pfd;
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredentialProvider::GetCredentialCount(
    DWORD* pdwCount, DWORD* pdwDefault, BOOL* pbAutoLogonWithDefault)
{
    __try
    {
        if (!pdwCount || !pdwDefault || !pbAutoLogonWithDefault)
            return E_INVALIDARG;

        *pdwCount = 1;
        *pdwDefault = 0;
        *pbAutoLogonWithDefault = FALSE;
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

IFACEMETHODIMP MfaSrvCredentialProvider::GetCredentialAt(
    DWORD dwIndex, ICredentialProviderCredential** ppcpc)
{
    __try
    {
        if (!ppcpc)
            return E_INVALIDARG;

        *ppcpc = NULL;

        if (dwIndex != 0 || !_pCredential)
            return E_INVALIDARG;

        return _pCredential->QueryInterface(IID_ICredentialProviderCredential, (void**)ppcpc);
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}

// ICredentialProviderSetUserArray
IFACEMETHODIMP MfaSrvCredentialProvider::SetUserArray(ICredentialProviderUserArray* users)
{
    __try
    {
        // We don't enumerate existing users - we provide our own tile.
        UNREFERENCED_PARAMETER(users);
        return S_OK;
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        return E_UNEXPECTED;
    }
}
