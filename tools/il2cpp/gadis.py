# gadis.py <nativemap.txt> <Type::Method or rva> [field types, comma separated]
# Disassembles one method of the game's GameAssembly.dll (function range from .pdata), naming called methods and
# float constants. The map comes from the dev command `nativemap <file>`. Field offsets of the listed types are
# shown next to [reg + offset] operands (ambiguous: an offset is printed for every listed type that has it).
import sys, re, struct, pefile
from capstone import Cs, CS_ARCH_X86, CS_MODE_64

DLL = r'G:/Games/CloudpunkVR/GameAssembly.dll'
pe = pefile.PE(DLL, fast_load=True)
pe.parse_data_directories(directories=[pefile.DIRECTORY_ENTRY['IMAGE_DIRECTORY_ENTRY_EXCEPTION']])
data = open(DLL, 'rb').read()

def rva2off(rva):
    for s in pe.sections:
        if s.VirtualAddress <= rva < s.VirtualAddress + max(s.Misc_VirtualSize, s.SizeOfRawData):
            return rva - s.VirtualAddress + s.PointerToRawData

names, fields = {}, {}
for line in open(sys.argv[1], encoding='utf-8'):
    p = line.rstrip('\n').split('\t')
    if p[0] == 'field':
        t, f = p[1].rsplit('::', 1)
        fields.setdefault(t, {})[int(p[2], 16)] = f
    elif len(p) == 2:
        names.setdefault(int(p[0], 16), p[1])

target = sys.argv[2]
if re.fullmatch(r'[0-9a-fA-F]+', target):
    rva = int(target, 16)
else:
    hits = [a for a, n in names.items() if n.startswith(target)]
    if not hits: sys.exit('not found: ' + target)
    for a in hits[1:]: print(f'; also {names[a]} at {a:x}')
    rva = hits[0]
ftypes = sys.argv[3].split(',') if len(sys.argv) > 3 else []

end = None
for e in getattr(pe, 'DIRECTORY_ENTRY_EXCEPTION', []):
    if e.struct.BeginAddress <= rva < e.struct.EndAddress:
        end = e.struct.EndAddress; break
end = end or rva + 0x800
print(f'; {names.get(rva, "?")} {rva:x}..{end:x}')

md = Cs(CS_ARCH_X86, CS_MODE_64)
off = rva2off(rva)
for ins in md.disasm(data[off:off + (end - rva)], rva):
    s = f'{ins.address:x}: {ins.mnemonic} {ins.op_str}'
    m = re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', ins.op_str)
    if m:
        tgt = ins.address + ins.size + int(m.group(2), 16) * (1 if m.group(1) == '+' else -1)
        o = rva2off(tgt)
        if o is not None and ('xmm' in ins.op_str or ins.mnemonic.endswith('ss')):
            s += f'    ; [{tgt:x}] = {struct.unpack("<f", data[o:o + 4])[0]:g}'
        elif o is not None and ins.mnemonic == 'lea':
            mm = re.match(rb'([\x20-\x7e]{4,})\x00', data[o:o + 200])
            if mm: s += f'    ; "{mm.group(1).decode()}"'
    if ins.mnemonic in ('call', 'jmp') and ins.op_str.startswith('0x'):
        t = int(ins.op_str, 16)
        if t in names: s += f'    ; {names[t]}'
    for mm in re.finditer(r'\[(r\w+) \+ (0x[0-9a-f]+)\]', ins.op_str):
        o = int(mm.group(2), 16)
        hit = [f'{t.split(".")[-1]}.{fields[t][o]}' for t in ftypes if o in fields.get(t, {})]
        if hit: s += '    ; ' + ' | '.join(hit)
    print(s)
