import random, math, statistics as st
def quant(v,p):
    v=sorted(v)
    if not v: return float('nan')
    k=(len(v)-1)*p; lo=int(k); hi=min(lo+1,len(v)-1)
    return v[lo]+(v[hi]-v[lo])*(k-lo)
def mwu_p(a,b,N=20000,seed=1):
    """permutation test on difference of medians"""
    rnd=random.Random(seed); obs=abs(st.median(a)-st.median(b)); pool=list(a)+list(b); na=len(a); c=0
    for _ in range(N):
        rnd.shuffle(pool)
        if abs(st.median(pool[:na])-st.median(pool[na:]))>=obs-1e-12: c+=1
    return (c+1)/(N+1)
def sign_test(pairs):
    """two-sided exact sign test on (x,y) pairs; returns (n_pos,n_neg,p)"""
    pos=sum(1 for x,y in pairs if y>x); neg=sum(1 for x,y in pairs if y<x); n=pos+neg
    if n==0: return pos,neg,1.0
    k=min(pos,neg)
    p=2*sum(math.comb(n,i) for i in range(k+1))/2**n
    return pos,neg,min(p,1.0)
