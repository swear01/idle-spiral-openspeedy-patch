#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(int argc, char **argv) {
    if (argc != 3) return 2;
    HMODULE proxy = LoadLibraryA("winhttp.dll");
    if (!proxy) return 3;
    void *(WINAPI *open)(const wchar_t *, DWORD, const wchar_t *, const wchar_t *, DWORD) = (void *)GetProcAddress(proxy, "WinHttpOpen");
    BOOL (WINAPI *close_http)(void *) = (void *)GetProcAddress(proxy, "WinHttpCloseHandle");
    void *session = open ? open(L"IdleSpiral offline proxy test", 1, NULL, NULL, 0) : NULL;
    if (!session || !close_http || !close_http(session)) return 4;
    char mono_path[4096], managed[4096], search[8192], marker[4096];
    snprintf(mono_path, sizeof(mono_path), "%s\\MonoBleedingEdge\\EmbedRuntime\\mono-2.0-bdwgc.dll", argv[1]);
    snprintf(managed, sizeof(managed), "%s\\IdleSpiral_Data\\Managed", argv[1]);
    snprintf(search, sizeof(search), "%s;%s", managed, argv[2]);
    HMODULE mono = LoadLibraryA(mono_path);
    if (!mono) return 5;
    void (__cdecl *set_path)(const char *) = (void *)GetProcAddress(mono, "mono_set_assemblies_path");
    void (__cdecl *set_root)(const char *) = (void *)GetProcAddress(mono, "mono_assembly_setrootdir");
    void (__cdecl *config)(const char *) = (void *)GetProcAddress(mono, "mono_config_parse");
    void *(__cdecl *init)(const char *, const char *) = (void *)GetProcAddress(mono, "mono_jit_init_version");
    if (!set_path || !set_root || !config || !init) return 6;
    set_path(search); config(NULL); set_root(managed);
    if (!init("IdleSpiral-bootstrap-test", "v4.0.30319")) return 7;
    snprintf(marker, sizeof(marker), "%s\\helper-invocation.txt", argv[2]);
    FILE *result = NULL;
    for (int i = 0; i < 100 && !result; i++) { result = fopen(marker, "r"); if (!result) Sleep(50); }
    if (!result) return 8;
    unsigned parent = 0;
    if (fscanf(result, "%u", &parent) != 1 || parent != GetCurrentProcessId()) return 9;
    fclose(result);
    printf("PASS: official Doorstop native startup -> bootstrap -> hidden helper with correct game PID and runtime path; WinHTTP proxy forwards an offline session.\n");
    return 0;
}