# Сравнение журналов стенда по полям (KRN): нормализует номера сущностей и элементов, пропускает поля времени и
# проверки из списка исключений; печатает различающиеся поля.  python3 tools/cmpfields.py old.log new.log [исключить-подстроку ...]
import json,re,sys
skip=sys.argv[3:]
def L(p):
    r=[]
    for l in open(p,encoding='utf-8',errors='replace'):
        if l.startswith('{') and not any(x in l for x in skip) and '"reads"' not in l:
            l=re.sub(r'#d[0-9a-f]{9}','#K',l); l=re.sub(r'элемент \d+','элемент N',l)
            l=re.sub(r'(TVertex|TShell|TFace|TEdge) \d+','\\1 N',l)
            try: r.append(json.loads(l))
            except: pass
    return r
a,b=L(sys.argv[1]),L(sys.argv[2]); n=0
for x,y in zip(a,b):
    ks=[k for k in set(x)|set(y) if x.get(k)!=y.get(k) and not re.search(r'ms$|Ms|time|elapsed|alloc|MB',k)]
    if not ks: continue
    n+=1
    for k in ks[:3]:
        sx,sy=str(x.get(k)),str(y.get(k)); i=next((i for i in range(min(len(sx),len(sy))) if sx[i]!=sy[i]),min(len(sx),len(sy)))
        print(f"{x.get('test') or x.get('kind') or '-'} [{k}] {sx[max(0,i-40):i+90]}  ||  {sy[max(0,i-40):i+90]}")
print(f"records {len(a)} vs {len(b)}; differing {n}")
