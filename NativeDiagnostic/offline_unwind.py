"""Unwind a local x64 minidump in this helper; never load or execute its DLLs."""
import bisect
import ctypes as C
import json
import pathlib
import struct
import sys

data = pathlib.Path(sys.argv[1]).read_bytes()
u32 = lambda p: struct.unpack_from('<I', data, p)[0]
u64 = lambda p: struct.unpack_from('<Q', data, p)[0]
assert data[:4] == b'MDMP'
streams = {u32(p): (u32(p+4), u32(p+8)) for p in range(u32(12), u32(12)+u32(8)*12, 12)}
kernel = C.WinDLL('kernel32', use_last_error=True)
kernel.VirtualAlloc.argtypes = [C.c_void_p, C.c_size_t, C.c_ulong, C.c_ulong]
kernel.VirtualAlloc.restype = C.c_void_p
kernel.GetModuleHandleW.argtypes = [C.c_wchar_p]
kernel.GetModuleHandleW.restype = C.c_void_p
unwind = C.WinDLL('ntdll').RtlVirtualUnwind
unwind.argtypes = [C.c_ulong, C.c_ulonglong, C.c_ulonglong, C.c_void_p, C.c_void_p,
                   C.POINTER(C.c_void_p), C.POINTER(C.c_ulonglong), C.c_void_p]
unwind.restype = C.c_void_p

def reserve(base, size):
    pointer = kernel.VirtualAlloc(base, size, 0x3000, 0x04)
    if pointer != base:
        raise RuntimeError('Cannot map dump address in helper: %x, error %d' % (base, C.get_last_error()))

modules = []
pos = streams[4][1]
for index in range(u32(pos)):
    p = pos+4+index*108
    name = u32(p+20)
    path = data[name+4:name+4+u32(name)].decode('utf-16le')
    modules.append(dict(base=u64(p), size=u32(p+8), timestamp=u32(p+16), path=path))

def module_at(address):
    return next((m for m in modules if m['base'] <= address < m['base']+m['size']), None)

def prepare(module):
    if 'functions' in module:
        return
    raw = pathlib.Path(module['path']).read_bytes()
    dword = lambda p: struct.unpack_from('<I', raw, p)[0]
    word = lambda p: struct.unpack_from('<H', raw, p)[0]
    pe = dword(0x3c)
    if raw[pe:pe+4] != b'PE\0\0' or word(pe+24) != 0x20b or dword(pe+8) != module['timestamp']:
        raise RuntimeError('Module does not match dump: '+pathlib.PureWindowsPath(module['path']).name)
    opt = pe+24
    sections = [(dword(p+12), dword(p+8), dword(p+20), dword(p+16))
                for p in range(opt+word(pe+20), opt+word(pe+20)+word(pe+6)*40, 40)]
    def offset(rva):
        for va, virtual_size, file_offset, file_size in sections:
            if va <= rva < va+max(virtual_size, file_size):
                return file_offset+rva-va
        return rva
    base = module['base']
    loaded = kernel.GetModuleHandleW(pathlib.PureWindowsPath(module['path']).name)
    if loaded != base:
        reserve(base, module['size'])
        C.memmove(base, raw[:dword(opt+60)], dword(opt+60))
        for va, _, file_offset, file_size in sections:
            C.memmove(base+va, raw[file_offset:file_offset+file_size], file_size)
    function_rva, function_size = struct.unpack_from('<II', raw, opt+112+3*8)
    module['functions'] = [(*struct.unpack_from('<III', raw, offset(function_rva)+i*12), base+function_rva+i*12)
                           for i in range(function_size//12)]
    module['begins'] = [f[0] for f in module['functions']]
    module['exports'] = {}
    export_rva = dword(opt+112)
    if export_rva:
        exp = offset(export_rva)
        funcs, names, ordinals = [dword(exp+n) for n in (28, 32, 36)]
        for i in range(dword(exp+24)):
            name_offset = offset(dword(offset(names)+4*i))
            name = raw[name_offset:raw.index(b'\0', name_offset)].decode('ascii', errors='replace')
            function = dword(offset(funcs)+4*word(offset(ordinals)+2*i))
            module['exports'][function] = name

thread_pos = streams[3][1]
target = int(sys.argv[3])
thread = next(thread_pos+4+i*48 for i in range(u32(thread_pos)) if u32(thread_pos+4+i*48) == target)
stack_base, stack_size, stack_rva = u64(thread+24), u32(thread+32), u32(thread+36)
allocation_base = stack_base & ~0xffff
reserve(allocation_base, stack_base+stack_size-allocation_base)
C.memmove(stack_base, data[stack_rva:stack_rva+stack_size], stack_size)
context_size, context_rva = u32(thread+40), u32(thread+44)
buffer = C.create_string_buffer(context_size+16)
context = (C.addressof(buffer)+15) & ~15
C.memmove(context, data[context_rva:context_rva+context_size], context_size)
frames = []
for _ in range(96):
    rip = C.c_ulonglong.from_address(context+248).value
    rsp = C.c_ulonglong.from_address(context+152).value
    module = module_at(rip)
    if not module or not stack_base <= rsp < stack_base+stack_size:
        frames.append(dict(stop='unknown code or stack boundary', code=hex(rip)))
        break
    prepare(module)
    rva = rip-module['base']
    index = bisect.bisect_right(module['begins'], rva)-1
    function = module['functions'][index] if index >= 0 else None
    function = function if function and function[0] <= rva < function[1] else None
    frame = dict(module=pathlib.PureWindowsPath(module['path']).name, rva=hex(rva))
    if function:
        frame['functionRva'] = hex(function[0])
        frame['export'] = module['exports'].get(function[0])
    frames.append(frame)
    if function:
        handler, establisher = C.c_void_p(), C.c_ulonglong()
        unwind(0, module['base'], rip, function[3], context, C.byref(handler), C.byref(establisher), None)
    else:
        C.c_ulonglong.from_address(context+248).value = C.c_ulonglong.from_address(rsp).value
        C.c_ulonglong.from_address(context+152).value = rsp+8
    if C.c_ulonglong.from_address(context+152).value <= rsp:
        frames.append(dict(stop='stack did not advance'))
        break
pathlib.Path(sys.argv[2]).write_text(json.dumps(frames, indent=2), encoding='utf-8')
print(json.dumps(frames, indent=2))
