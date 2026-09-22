**From:** overview-
**Timestamp:** 2026-09-22T05:06:47.7156520+01:00
**Priority:** normal

# operator-answered-fixed-mapping-in-config-domain

Decision recorded (Q4 — back-end routing): **fixed mapping in config** — domains/recipients map to back-end hosts; explicit, no external dependency; changes require reload or deploy.

Action: folded into `.styloagent/spec.md` §7.
Result: no directory dependency sits in the delivery path, so a directory outage cannot stall the queue — a natural fit with the store-and-forward decision in Q3. Consistent with the source specification's "behind an established MTA/application connector" posture and its single configured upstream for the weekend demonstrator.
Next step: spec awaiting operator sign-off; architecture next. Note that the wire protocol to the back-end (SMTP vs LMTP-into-store) remains uncovered and will be asked or decided at architecture time.
