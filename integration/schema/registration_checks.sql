BEGIN;
DO $test$
DECLARE
    k1 uuid := gen_random_uuid(); k2 uuid := gen_random_uuid(); o1 uuid := gen_random_uuid();
BEGIN
    INSERT INTO identity.idempotency_records(id,scope_key,operation,idempotency_key,request_hash,expires_at)
    VALUES (k1,'test','create_user',k1::text,decode(repeat('aa',32),'hex'),CURRENT_TIMESTAMP + interval '24 hours'),
           (k2,'test','create_user',k2::text,decode(repeat('bb',32),'hex'),CURRENT_TIMESTAMP + interval '24 hours');
    INSERT INTO identity.registration_operations(id,created_at,updated_at,normalized_email,idempotency_record_id,correlation_id,stage)
    VALUES (o1,CURRENT_TIMESTAMP,CURRENT_TIMESTAMP,'registration@example.test',k1,gen_random_uuid(),'reserved');
    BEGIN
        INSERT INTO identity.registration_operations(id,created_at,updated_at,normalized_email,idempotency_record_id,correlation_id,stage)
        VALUES (gen_random_uuid(),CURRENT_TIMESTAMP,CURRENT_TIMESTAMP,'registration@example.test',k2,gen_random_uuid(),'reserved');
        RAISE EXCEPTION 'Duplicate active registration accepted';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    BEGIN
        UPDATE identity.registration_operations SET stage='completed' WHERE id=o1;
        RAISE EXCEPTION 'Incomplete success accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        DELETE FROM identity.idempotency_records WHERE id=k1;
        RAISE EXCEPTION 'Pending operation lost its idempotency anchor';
    EXCEPTION WHEN foreign_key_violation THEN NULL;
    END;
    UPDATE identity.registration_operations SET stage='reconciliation_required' WHERE id=o1;
    BEGIN
        INSERT INTO identity.registration_operations(id,created_at,updated_at,normalized_email,idempotency_record_id,correlation_id,stage)
        VALUES (gen_random_uuid(),CURRENT_TIMESTAMP,CURRENT_TIMESTAMP,'registration@example.test',k2,gen_random_uuid(),'reserved');
        RAISE EXCEPTION 'Uncertain operation did not reserve email';
    EXCEPTION WHEN unique_violation THEN NULL;
    END;
    UPDATE identity.registration_operations SET stage='failed' WHERE id=o1;
    INSERT INTO identity.registration_operations(id,created_at,updated_at,normalized_email,idempotency_record_id,correlation_id,stage)
    VALUES (gen_random_uuid(),CURRENT_TIMESTAMP,CURRENT_TIMESTAMP,'registration@example.test',k2,gen_random_uuid(),'reserved');
END;
$test$;
ROLLBACK;
