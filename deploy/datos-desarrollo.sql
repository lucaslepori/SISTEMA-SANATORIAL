-- Datos mínimos para desarrollo local: dos empresas y tres usuarios de prueba.
-- No usar en producción.
INSERT INTO nucleo.empresas (id, razon_social, cuit) VALUES
    ('0192f000-0000-7000-8000-000000000001', 'Clínica Ejemplo S.A.', '30000000001'),
    ('0192f000-0000-7000-8000-000000000002', 'Sanatorio Prueba S.R.L.', '30000000002')
ON CONFLICT DO NOTHING;

INSERT INTO nucleo.usuarios (id, sujeto_oidc, nombre_mostrar) VALUES
    ('0192f000-0000-7000-8000-0000000000a1', 'admision.local', 'Admisión (local)'),
    ('0192f000-0000-7000-8000-0000000000a2', 'medica.local', 'Dra. Local'),
    ('0192f000-0000-7000-8000-0000000000a3', 'admin.local', 'Administrador (local)')
ON CONFLICT DO NOTHING;

INSERT INTO nucleo.usuarios_empresas (usuario_id, empresa_id, roles) VALUES
    ('0192f000-0000-7000-8000-0000000000a1', '0192f000-0000-7000-8000-000000000001', '{admision}'),
    ('0192f000-0000-7000-8000-0000000000a2', '0192f000-0000-7000-8000-000000000001', '{profesional}'),
    ('0192f000-0000-7000-8000-0000000000a3', '0192f000-0000-7000-8000-000000000001', '{administrador}'),
    ('0192f000-0000-7000-8000-0000000000a3', '0192f000-0000-7000-8000-000000000002', '{administrador}')
ON CONFLICT DO NOTHING;
