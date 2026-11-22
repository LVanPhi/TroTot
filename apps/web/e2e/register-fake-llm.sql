-- E2E only (docs/05-ai-worker.md, Sổ đăng ký mô hình): register and activate the deterministic fake LLM used with
-- LLM_FAKE=1. Never part of a migration or the demo seed, so it can never exist in production.
-- Run as app_migrator: psql "$MIGRATOR_URL" -f apps/web/e2e/register-fake-llm.sql
SET search_path = app, public;
INSERT INTO app.model_versions (component, name, version)
VALUES ('llm', 'fake/deterministic', 'prompt-v1')
ON CONFLICT (component, name, version) DO NOTHING;
UPDATE app.model_versions SET is_active = false WHERE component = 'llm' AND is_active AND name <> 'fake/deterministic';
UPDATE app.model_versions SET is_active = true, activated_at = now()
WHERE component = 'llm' AND name = 'fake/deterministic' AND NOT is_active;
