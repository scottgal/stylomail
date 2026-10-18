#!/usr/bin/env python3
"""Harness-only profile history rows.  This never talks to a running Host."""
import datetime as dt
import json
import sqlite3
import sys

db = sys.argv[1]
now = dt.datetime.now(dt.timezone.utc).replace(microsecond=0)
current = now - dt.timedelta(days=30)
prior = now - dt.timedelta(days=120)
tenant, entity = "harness", "harness"

def stamp(when): return when.isoformat(timespec="microseconds").replace("+00:00", "Z")

def payload(assessment_id, message_id, when, action, signals, recipient=None):
    evidence = [{"signalId": signal, "origin": "Semantic", "availability": "Available",
                 "value": value, "confidence": None, "sampleSupport": None,
                 "sourceVersion": "governed-harness/1", "observedAt": stamp(when),
                 "observedScope": None, "attributes": []} for signal, value in signals]
    dispositions = [] if recipient is None else [{"recipient": recipient, "recipientRisk": 0.0,
        "action": "Allow", "deliveryState": "Queued", "reEvaluateBy": None,
        "recipientScopedSignalIds": None}]
    return json.dumps({"assessmentId": assessment_id, "internalMessageId": message_id,
        "tenantId": tenant, "channel": {"kind":"Email"}, "evidence": evidence,
        "riskDimensions": [], "riskIndex": 0.0, "riskIndexDenominator": 0.0,
        "coveredWeightFraction": 0.0, "action": action, "deliveryTiming":"PostDelivery",
        "reasons": [], "versions": {"policyVersion":"governed-harness/1",
        "classifierModelVersion":None,"questionSchemaVersion":"governed-harness/1",
        "preprocessingVersion":"governed-harness/1","profileVersions":None,"regimeId":None},
        "coverage":{"bodyParsed":True,"htmlPresent":False,"hasAttachments":False,
        "htmlTextDisagreement":False,"parserLimitExceeded":False,"oversizeRejected":False,
        "contentEncrypted":False,"truncated":False,"conversationContextMissing":True},
        "recipientDispositions":dispositions,"assessedAt":stamp(when)}, separators=(",", ":"))

con = sqlite3.connect(db)
cur = con.cursor()
cur.execute("INSERT INTO company VALUES (?,?,?,?,?,?)", (tenant,"co_harness","Harness Corporation",None,"governed-harness",stamp(now)))
cur.execute("INSERT INTO sender_profile VALUES (?,?,?,?,?,?,?,?,?,?)", (tenant,entity,"Harness Billing","co_harness",None,None,None,None,"governed-harness",stamp(now)))
cur.execute("INSERT INTO sender_profile VALUES (?,?,?,?,?,?,?,?,?,?)", (tenant,"thin-history","Thin History",None,None,None,None,None,"governed-harness",stamp(now)))

def add(prefix, when, direction, action="Allow", signals=(), recipient=None):
    aid, mid = "asm-"+prefix, "msg-"+prefix
    cur.execute("INSERT INTO host_decision_ledger VALUES (?,?,?,?,?,?,?,?,?,?)",
        (tenant, aid, mid, action, stamp(when), "Email", None, None, None,
         payload(aid, mid, when, action, signals, recipient)))
    cur.execute("INSERT INTO queue_item VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
        ("q-"+prefix,tenant,mid,"governed://"+prefix,"digest-"+prefix,direction,
         "owner@harness.test" if direction else recipient,entity,None,0,None,4,0,None,
         None,None,None,None,None,None,stamp(when),stamp(when)))

for window, start, replies, holds in (("prior", prior, {0}, {0,1,2,3}), ("current", current, {0,1,2,3}, {0,1})):
    for i in range(5):
        when = start + dt.timedelta(days=i)
        person = f"person{i}@example.test"
        signals = [("semantic.urgency_pressure", 0.9 if i < 3 else 0.1)]
        add(f"{window}-in-{i}", when, 0, "Hold" if i in holds else "Allow", signals, person)
        if i in replies:
            add(f"{window}-out-{i}", when + dt.timedelta(hours=2), 1, "Allow", (), person)

# A thin entity exists as rows, but cannot satisfy the five-message pressure minimum.
for i in range(2):
    aid, mid, when = f"asm-thin-{i}", f"msg-thin-{i}", now-dt.timedelta(days=10+i)
    cur.execute("INSERT INTO host_decision_ledger VALUES (?,?,?,?,?,?,?,?,?,?)", (tenant, aid, mid, "Allow", stamp(when), "Email", None, None, None, payload(aid, mid, when, "Allow", [("semantic.urgency_pressure",0.9)])))
    cur.execute("INSERT INTO queue_item VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)", (f"q-thin-{i}",tenant,mid,f"governed://thin-{i}",f"digest-thin-{i}",0,f"thin{i}@example.test","thin-history",None,0,None,4,0,None,None,None,None,None,None,None,stamp(when),stamp(when)))
con.commit()
con.close()
print("Seeded governed profile history: produced trend, thin history, and no-row entity are addressable.")
