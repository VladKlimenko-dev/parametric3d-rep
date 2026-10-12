# Сравнение двух журналов стенда запись в запись (без полей времени): python3 tools/cmplog.py old.log new.log
import json,re,sys
def L(p):
    out=[]
    for l in open(p,encoding='utf-8',errors='replace'):
        l=l.strip()
        if l.startswith('{'):
            try: d=json.loads(l); out.append(json.dumps({k:v for k,v in d.items() if not re.search(r'ms|Ms|time|Time|elapsed|args|alloc|MB|stages|dbgFace|dbgArc|^fi$|^areas$|^face$',k)},sort_keys=True))
            except: pass
        elif l.startswith(('OK','FAIL')): out.append(re.sub(r'\s*\[\d+ ms\]|in \d+ ms','',l))
    return out
a,b=L(sys.argv[1]),L(sys.argv[2]); d=[(x,y) for x,y in zip(a,b) if x!=y]
print(sys.argv[1],'vs',sys.argv[2],':',len(a),len(b),'differing',len(d))
for x,y in d[:3]: print(' -',x[:180]); print(' +',y[:180])
