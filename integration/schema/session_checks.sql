BEGIN;
DO $$
DECLARE profile uuid; session_id uuid := gen_random_uuid();
BEGIN
    INSERT INTO identity.users(cognito_sub,email) VALUES ('session-schema-check','session-schema@example.test') RETURNING id INTO profile;
    INSERT INTO identity.login_attempts(email_hash) VALUES (decode(repeat('ab',32),'hex'));
    INSERT INTO identity.sessions(id,user_id,created_at,updated_at,expires_at,access_expires_at,access_hash,refresh_hash,state)
    VALUES(session_id,profile,now(),now(),now()+interval '7 days',now()+interval '15 minutes',
        decode(repeat('ab',32),'hex'),decode(repeat('cd',32),'hex'),'active');
    BEGIN
        UPDATE identity.sessions SET state='revoked' WHERE id=session_id;
        RAISE EXCEPTION 'revoked session without timestamp accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    BEGIN
        UPDATE identity.sessions SET state='refreshing' WHERE id=session_id;
        RAISE EXCEPTION 'refresh without owner accepted';
    EXCEPTION WHEN check_violation THEN NULL;
    END;
    UPDATE identity.sessions SET state='refreshing',refresh_owner=gen_random_uuid() WHERE id=session_id;
    UPDATE identity.sessions SET state='revoked',refresh_owner=NULL,revoked_at=now() WHERE id=session_id;
END $$;
ROLLBACK;
