#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
int main(int argc, char **argv) {
    if (argc != 4 || strcmp(argv[1], "autoload") || !strstr(argv[3], "IdleSpiral.TheoryGuard.dll") || GetFileAttributesA(argv[3]) == INVALID_FILE_ATTRIBUTES) return 2;
    FILE *result = fopen("helper-invocation.txt", "w");
    if (!result) return 3;
    fprintf(result, "%u\n", (unsigned)strtoul(argv[2], NULL, 10));
    fclose(result);
    return 0;
}
