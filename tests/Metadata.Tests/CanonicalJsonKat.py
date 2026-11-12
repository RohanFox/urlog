# Independent oracle for the known-answer vectors of CanonicalJson / AuditHash.
# Python's json.dumps(sort_keys=True, separators=(',', ':'), ensure_ascii=False) matches the canonical rules for BMP-only
# keys (Python sorts by code point, the C# writer by UTF-16 unit: identical when no key leaves the BMP), lowercase \u00xx
# escapes, short escapes for \b \t \n \f \r, raw DEL and raw non-ASCII.
import hashlib, json

def canon(o):
    return json.dumps(o, sort_keys=True, separators=(',', ':'), ensure_ascii=False).encode('utf-8')

def row(seq, ts, actor, actor_id, action, target_type, target_id, details, prev, schema_version):
    return {"seq": seq, "ts_utc": ts, "actor": actor, "actor_id": actor_id, "action": action, "target_type": target_type,
            "target_id": target_id, "details_json": details, "prev_hash": prev, "schema_version": schema_version}

def link(prev, r):
    return hashlib.sha256(prev.encode('ascii') + canon(r)).hexdigest()

ZERO = '0' * 64
genesis_details = canon({"migration": "20261106171503_Initial", "tool": "urlog 1.0.0.0"}).decode()
g = row(1, "2026-11-06T12:00:00.000000Z", "system", None, "genesis", None, None, genesis_details, ZERO, 1)
gh = link(ZERO, g)
print("genesis details:", genesis_details)
print("genesis canonical:", canon(g).decode())
print("genesis hash:", gh)

d2 = canon({"case_name": "incident 1"}).decode()
r2 = row(2, "2026-11-06T12:34:56.123456Z", "alice", 1, "case_create", "case", 1, d2, gh, None)
h2 = link(gh, r2)
print("row2 canonical:", canon(r2).decode())
print("row2 hash:", h2)

# Unicode and escapes inside details (values only; keys stay ASCII).
d3 = canon({"name": "é€😀 \u0000\u001f\"\\\n\r\t\b\f/\u007f ", "n": -9223372036854775808, "u": 18446744073709551615, "ok": True, "none": None}).decode()
r3 = row(3, "2026-11-06T12:34:57.000001Z", "bob", 2, "file_intake", "file", 7, d3, h2, None)
h3 = link(h2, r3)
print("row3 details hex:", d3.encode("utf-8").hex())
print("row3 canonical hex:", canon(r3).hex())
print("row3 hash:", h3)
