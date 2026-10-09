-- Run only against the isolated database created by validate_schema.py.
-- Fixtures are synthetic and always rolled back.
BEGIN;

CREATE FUNCTION pg_temp.expect_error(statement text, expected_state text) RETURNS void
LANGUAGE plpgsql AS $test$
BEGIN
    BEGIN
        EXECUTE statement;
    EXCEPTION WHEN OTHERS THEN
        IF SQLSTATE = expected_state THEN RETURN; END IF;
        RAISE EXCEPTION 'Expected SQLSTATE %, received %: %', expected_state, SQLSTATE, SQLERRM;
    END;
    RAISE EXCEPTION 'Expected SQLSTATE %, but statement succeeded: %', expected_state, statement;
END;
$test$;

DO $test$
DECLARE
    u1 uuid := gen_random_uuid(); u2 uuid := gen_random_uuid(); u3 uuid := gen_random_uuid();
    h1 uuid := gen_random_uuid(); h2 uuid := gen_random_uuid();
    m1 uuid := gen_random_uuid(); m2 uuid := gen_random_uuid(); m3 uuid := gen_random_uuid();
    p1 uuid := gen_random_uuid(); p2 uuid := gen_random_uuid();
    c1 uuid := gen_random_uuid(); c2 uuid := gen_random_uuid(); c3 uuid := gen_random_uuid();
    pc1 uuid := gen_random_uuid(); pc2 uuid := gen_random_uuid();
    e1 uuid := gen_random_uuid(); pp1 uuid := gen_random_uuid(); pp2 uuid := gen_random_uuid();
    pp_other uuid := gen_random_uuid(); b1 uuid := gen_random_uuid(); be1 uuid := gen_random_uuid();
    src1 uuid := gen_random_uuid(); src2 uuid := gen_random_uuid(); inc1 uuid := gen_random_uuid();
    v1 uuid := gen_random_uuid(); v2 uuid := gen_random_uuid();
    payload jsonb; before_snapshot jsonb;
BEGIN
    INSERT INTO identity.users(id,cognito_sub,email) VALUES
        (u1,u1::text,'one@example.test'),(u2,u2::text,'two@example.test'),(u3,u3::text,'three@example.test');
    PERFORM pg_temp.expect_error(format('INSERT INTO identity.users(cognito_sub,email) VALUES (%L,%L)', gen_random_uuid()::text,' ONE@example.test '),'23505');
    INSERT INTO household.households(id,name,created_by_user_id) VALUES (h1,'House A',u1),(h2,'House B',u1);
    INSERT INTO household.household_members(id,household_id,user_id,role,financial_position) VALUES
        (m1,h1,u1,'household_admin',1),(m2,h1,u2,'financial_member',2),(m3,h2,u1,'household_admin',1);
    SET CONSTRAINTS ALL IMMEDIATE;
    PERFORM pg_temp.expect_error(format('UPDATE household.household_members SET left_at = CURRENT_TIMESTAMP WHERE id = %L',m1),'23514');
    PERFORM pg_temp.expect_error(format('INSERT INTO household.household_members(household_id,user_id,role,financial_position) VALUES (%L,%L,%L,2)',h1,u3,'financial_member'),'23505');
    PERFORM pg_temp.expect_error(format('INSERT INTO household.households(name,created_by_user_id) VALUES (%L,%L)','Without admin',u1),'23514');

    INSERT INTO finance.monthly_periods(id,household_id,reference_month,created_by_user_id) VALUES
        (p1,h1,'2026-01-01',u1),(p2,h1,'2026-02-01',u1);
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.monthly_periods(household_id,reference_month,created_by_user_id) VALUES (%L,%L,%L)',h1,'2026-01-01',u1),'23505');
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.monthly_periods(household_id,reference_month,created_by_user_id) VALUES (%L,%L,%L)',h1,'2026-03-02',u1),'23514');
    PERFORM pg_temp.expect_error(format('UPDATE finance.monthly_periods SET reference_month=%L WHERE id=%L','2026-04-01',p1),'23514');
    INSERT INTO finance.expense_categories(id,household_id,name) VALUES (c1,h1,'Market'),(c2,h2,'Market');
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.expense_categories(household_id,name) VALUES (%L,%L)',h1,' MARKET '),'23505');
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.period_categories(household_id,monthly_period_id,expense_category_id,name_snapshot) VALUES (%L,%L,%L,%L)',h1,p1,c2,'Other house'),'23503');
    INSERT INTO finance.period_categories(id,household_id,monthly_period_id,expense_category_id,name_snapshot)
        VALUES (pc1,h1,p1,c1,'Market');
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.expense_entries(household_id,monthly_period_id,period_category_id,amount) VALUES (%L,%L,%L,10)',h1,p2,pc1),'23503');
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.expense_entries(household_id,monthly_period_id,period_category_id,amount) VALUES (%L,%L,%L,-1)',h1,p1,pc1),'23514');
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.expense_entries(household_id,monthly_period_id,period_category_id,amount) VALUES (%L,%L,%L,%L)',h1,p1,pc1,'NaN'),'23514');
    INSERT INTO finance.expense_entries(id,household_id,monthly_period_id,period_category_id,amount) VALUES (e1,h1,p1,pc1,10);
    INSERT INTO finance.expense_entries(household_id,monthly_period_id,period_category_id,amount) VALUES (h1,p1,pc1,20);
    UPDATE finance.period_categories SET payment_status='paid',paid_at=CURRENT_TIMESTAMP,paid_by_user_id=u1 WHERE id=pc1;
    UPDATE finance.expense_entries SET note='Note only' WHERE id=e1;
    IF (SELECT payment_status FROM finance.period_categories WHERE id=pc1) <> 'paid' THEN RAISE EXCEPTION 'Note reset payment'; END IF;
    UPDATE finance.expense_entries SET amount=12 WHERE id=e1;
    IF (SELECT payment_status FROM finance.period_categories WHERE id=pc1) <> 'pending' THEN RAISE EXCEPTION 'Amount did not reset payment'; END IF;
    INSERT INTO finance.expense_categories(id,household_id,name) VALUES (c3,h1,'Transport');
    INSERT INTO finance.period_categories(id,household_id,monthly_period_id,expense_category_id,name_snapshot)
        VALUES (pc2,h1,p1,c3,'Transport');
    UPDATE finance.period_categories SET payment_status='paid',paid_at=CURRENT_TIMESTAMP,paid_by_user_id=u1 WHERE id IN (pc1,pc2);
    UPDATE finance.expense_entries SET period_category_id=pc2 WHERE id=e1;
    IF EXISTS(SELECT 1 FROM finance.period_categories WHERE id IN (pc1,pc2) AND payment_status <> 'pending') THEN RAISE EXCEPTION 'Category move did not reset both payments'; END IF;
    UPDATE finance.expense_entries SET period_category_id=pc1 WHERE id=e1;
    UPDATE finance.period_categories SET payment_status='paid',paid_at=CURRENT_TIMESTAMP,paid_by_user_id=u1 WHERE id=pc1;

    INSERT INTO finance.period_participants(id,household_id,monthly_period_id,household_member_id,financial_position,display_name_snapshot)
        VALUES (pp1,h1,p1,m1,1,'One'),(pp2,h1,p1,m2,2,'Two'),(pp_other,h1,p2,m1,1,'One');
    INSERT INTO finance.salary_entries(household_id,monthly_period_id,period_participant_id,amount) VALUES (h1,p1,pp1,0);
    IF EXISTS(SELECT 1 FROM finance.salary_entries WHERE period_participant_id=pp2) THEN RAISE EXCEPTION 'Missing salary was manufactured'; END IF;
    UPDATE finance.salary_entries SET amount=2000 WHERE period_participant_id=pp1;
    INSERT INTO finance.salary_entries(household_id,monthly_period_id,period_participant_id,amount) VALUES (h1,p1,pp2,3000);
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.salary_entries(household_id,monthly_period_id,period_participant_id,amount) VALUES (%L,%L,%L,10)',h1,p1,pp_other),'23503');
    INSERT INTO finance.benefits(id,household_id,name,application_order) VALUES (b1,h1,'Benefit',1);
    INSERT INTO finance.benefit_entries(id,household_id,monthly_period_id,benefit_id,name_snapshot,application_order,amount) VALUES (be1,h1,p1,b1,'Benefit',1,25);
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.period_benefit_priorities(household_id,monthly_period_id,benefit_entry_id,period_participant_id,priority) VALUES (%L,%L,%L,%L,1)',h1,p1,be1,pp_other),'23503');
    INSERT INTO finance.period_benefit_priorities(household_id,monthly_period_id,benefit_entry_id,period_participant_id,priority)
        VALUES (h1,p1,be1,pp1,1),(h1,p1,be1,pp2,2);

    INSERT INTO income.income_sources(id,household_id,name,source_type) VALUES (src1,h1,'Rent A','rental'),(src2,h1,'Rent B','rental');
    INSERT INTO income.income_entries(id,household_id,monthly_period_id,income_source_id,entry_type,amount,source_name_snapshot,status,confirmed_at)
        VALUES (inc1,h1,p1,src1,'recurring',100,'Rent A','confirmed',CURRENT_TIMESTAMP);
    INSERT INTO income.income_entries(household_id,monthly_period_id,income_source_id,entry_type,amount,source_name_snapshot,status,confirmed_at)
        VALUES (h1,p1,src1,'recurring',50,'Rent A','confirmed',CURRENT_TIMESTAMP);
    PERFORM pg_temp.expect_error(format('INSERT INTO income.income_entries(household_id,monthly_period_id,income_source_id,entry_type,amount,source_name_snapshot,status,suggested_from_entry_id) VALUES (%L,%L,%L,%L,100,%L,%L,%L)',h1,p2,src2,'recurring','Rent B','suggested',inc1),'23503');
    INSERT INTO income.income_entries(household_id,monthly_period_id,income_source_id,entry_type,amount,source_name_snapshot,status,suggested_from_entry_id)
        VALUES (h1,p2,src1,'recurring',100,'Rent A','suggested',inc1);
    IF (SELECT sum(amount) FROM income.income_entries WHERE monthly_period_id=p1 AND status='confirmed') <> 150 THEN RAISE EXCEPTION 'Recurring income multiplicity failed'; END IF;

    INSERT INTO finance.idempotency_records(household_id,actor_user_id,operation,idempotency_key,request_hash,expires_at)
        VALUES(h1,u1,'create-expense','test-key',decode(repeat('ab',32),'hex'),CURRENT_TIMESTAMP+interval '1 day');
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.idempotency_records(household_id,actor_user_id,operation,idempotency_key,request_hash,expires_at) VALUES (%L,%L,%L,%L,decode(repeat(%L,32),%L),CURRENT_TIMESTAMP+interval %L)',h1,u1,'create-expense','test-key','ab','hex','1 day'),'23505');

    SELECT jsonb_build_object(
        'categories',(SELECT jsonb_agg(to_jsonb(t)) FROM finance.period_categories t WHERE monthly_period_id=p1),
        'expenses',(SELECT jsonb_agg(to_jsonb(t)) FROM finance.expense_entries t WHERE monthly_period_id=p1),
        'payments',jsonb_build_array(jsonb_build_object('category_id',pc1,'status','paid')),
        'participants',(SELECT jsonb_agg(to_jsonb(t)) FROM finance.period_participants t WHERE monthly_period_id=p1),
        'salaries',(SELECT jsonb_agg(to_jsonb(t)) FROM finance.salary_entries t WHERE monthly_period_id=p1),
        'benefits',(SELECT jsonb_agg(to_jsonb(t)) FROM finance.benefit_entries t WHERE monthly_period_id=p1),
        'benefit_priorities',(SELECT jsonb_agg(to_jsonb(t)) FROM finance.period_benefit_priorities t WHERE monthly_period_id=p1),
        'income_sources',(SELECT jsonb_agg(to_jsonb(t)) FROM income.income_sources t WHERE household_id=h1),
        'incomes',(SELECT jsonb_agg(to_jsonb(t)) FROM income.income_entries t WHERE monthly_period_id=p1)) INTO payload;

    -- A closing version without its allocation cannot commit.
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.monthly_period_versions(household_id,monthly_period_id,closing_number,source_edit_version,closed_by_user_id,snapshot) VALUES (%L,%L,99,1,%L,%L::jsonb)',h1,p1,u1,payload),'23503');
    SET CONSTRAINTS ALL DEFERRED;
    INSERT INTO finance.monthly_period_versions(id,household_id,monthly_period_id,closing_number,source_edit_version,closed_by_user_id,snapshot)
        VALUES(v1,h1,p1,1,1,u1,payload);
    INSERT INTO finance.allocation_snapshots(id,household_id,commitment_percentage,cost_total,salary_total,benefit_total,benefit_applied,cash_total,total_committed,algorithm_version,rounding_policy,participants,benefit_applications)
        VALUES(v1,h1,1,32,5000,25,25,25,50,'test-v1','half-away-from-zero',
            jsonb_build_array(jsonb_build_object('participant_id',pp1,'salary','2000.00','base','20.00','benefit','20.00','cash','0.00'),jsonb_build_object('participant_id',pp2,'salary','3000.00','base','30.00','benefit','5.00','cash','25.00')),
            jsonb_build_array(jsonb_build_object('benefit_id',b1,'participant_id',pp1,'applied','20.00'),jsonb_build_object('benefit_id',b1,'participant_id',pp2,'applied','5.00')));
    SET CONSTRAINTS ALL IMMEDIATE;
    UPDATE finance.monthly_periods SET status='closed',latest_closed_version_id=v1,edit_version=2 WHERE id=p1;
    PERFORM pg_temp.expect_error(format('INSERT INTO finance.expense_entries(household_id,monthly_period_id,period_category_id,amount) VALUES (%L,%L,%L,1)',h1,p1,pc1),'23514');
    PERFORM pg_temp.expect_error(format('INSERT INTO income.income_entries(household_id,monthly_period_id,entry_type,amount,confirmed_at) VALUES (%L,%L,%L,1,CURRENT_TIMESTAMP)',h1,p1,'variable'),'23514');
    PERFORM pg_temp.expect_error(format('UPDATE finance.allocation_snapshots SET cash_total=0 WHERE id=%L',v1),'23514');
    PERFORM pg_temp.expect_error(format('DELETE FROM finance.monthly_period_versions WHERE id=%L',v1),'23514');
    PERFORM pg_temp.expect_error(format('UPDATE finance.monthly_periods SET latest_closed_version_id=%L WHERE id=%L',v1,p2),'23503');
    SELECT snapshot INTO before_snapshot FROM finance.monthly_period_versions WHERE id=v1;
    UPDATE finance.expense_categories SET name='New category name',is_active=false WHERE id=c1;
    UPDATE finance.monthly_periods SET status='draft',edit_version=3,last_reopened_at=CURRENT_TIMESTAMP,last_reopened_by_user_id=u1 WHERE id=p1;
    UPDATE finance.expense_entries SET note='Reopened' WHERE id=e1;
    IF (SELECT snapshot FROM finance.monthly_period_versions WHERE id=v1) IS DISTINCT FROM before_snapshot THEN RAISE EXCEPTION 'Historical snapshot changed'; END IF;
    IF (SELECT difference FROM finance.allocation_snapshots WHERE id=v1) <> 18 THEN RAISE EXCEPTION 'Allocation reconciliation failed'; END IF;

    -- Reclosing creates another version instead of overwriting the first snapshot.
    SET CONSTRAINTS ALL DEFERRED;
    INSERT INTO finance.monthly_period_versions(id,household_id,monthly_period_id,closing_number,source_edit_version,closed_by_user_id,snapshot)
        VALUES(v2,h1,p1,2,3,u1,payload);
    INSERT INTO finance.allocation_snapshots(id,household_id,commitment_percentage,cost_total,salary_total,benefit_total,benefit_applied,cash_total,total_committed,algorithm_version,rounding_policy,participants,benefit_applications)
        SELECT v2,household_id,commitment_percentage,cost_total,salary_total,benefit_total,benefit_applied,cash_total,total_committed,algorithm_version,rounding_policy,participants,benefit_applications
        FROM finance.allocation_snapshots WHERE id=v1;
    SET CONSTRAINTS ALL IMMEDIATE;
    UPDATE finance.monthly_periods SET status='closed',latest_closed_version_id=v2,edit_version=4 WHERE id=p1;
    IF (SELECT count(*) FROM finance.monthly_period_versions WHERE monthly_period_id=p1) <> 2 THEN RAISE EXCEPTION 'Reclosing lost history'; END IF;
    IF (SELECT snapshot FROM finance.monthly_period_versions WHERE id=v1) IS DISTINCT FROM before_snapshot THEN RAISE EXCEPTION 'Reclosing changed the first snapshot'; END IF;

    INSERT INTO finance.audit_logs(household_id,actor_user_id,action,entity_type,entity_id) VALUES(h1,u1,'reopen','monthly_period',p1);
    PERFORM pg_temp.expect_error(format('UPDATE finance.audit_logs SET action=%L WHERE household_id=%L','change-history',h1),'23514');
    RAISE NOTICE 'PASS: cross-house/month FKs, uniqueness, money, payments, participants, idempotency, closures and history.';
END;
$test$;

DO $test$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_class t JOIN pg_namespace n ON n.oid=t.relnamespace
        WHERE n.nspname IN ('identity','household','finance','income') AND t.relkind='r'
          AND NOT EXISTS(SELECT 1 FROM pg_constraint c WHERE c.conrelid=t.oid AND c.contype='p')
    ) THEN RAISE EXCEPTION 'Table without primary key'; END IF;
    IF EXISTS (
        SELECT 1 FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace
        WHERE c.contype='f' AND n.nspname IN ('identity','household','finance','income')
        AND NOT EXISTS (
            SELECT 1 FROM pg_index i WHERE i.indrelid=c.conrelid AND i.indisvalid AND i.indpred IS NULL
              AND i.indnkeyatts >= cardinality(c.conkey)
              AND ARRAY(SELECT k FROM unnest(i.indkey) WITH ORDINALITY AS x(k,pos) WHERE pos <= cardinality(c.conkey) ORDER BY k)
                = ARRAY(SELECT k FROM unnest(c.conkey) AS x(k) ORDER BY k)
        )
    ) THEN RAISE EXCEPTION 'Foreign key without a supporting index'; END IF;
    RAISE NOTICE 'PASS: all tables have primary keys and all foreign keys have supporting indexes.';
END;
$test$;

ROLLBACK;
