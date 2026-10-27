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

-- Catálogos y una agenda de ejemplo (lunes y miércoles de 9 a 12, turnos de 20 minutos).
INSERT INTO catalogos.servicios (id, empresa_id, nombre) VALUES
    ('0192f000-0000-7000-8000-0000000000b1', '0192f000-0000-7000-8000-000000000001', 'Clínica médica')
ON CONFLICT DO NOTHING;
INSERT INTO catalogos.especialidades (id, empresa_id, nombre) VALUES
    ('0192f000-0000-7000-8000-0000000000b2', '0192f000-0000-7000-8000-000000000001', 'Medicina interna')
ON CONFLICT DO NOTHING;
INSERT INTO catalogos.sedes (id, empresa_id, nombre, direccion) VALUES
    ('0192f000-0000-7000-8000-0000000000b3', '0192f000-0000-7000-8000-000000000001', 'Sede central', 'Calle Falsa 123')
ON CONFLICT DO NOTHING;
INSERT INTO catalogos.consultorios (id, empresa_id, sede_id, nombre) VALUES
    ('0192f000-0000-7000-8000-0000000000b4', '0192f000-0000-7000-8000-000000000001', '0192f000-0000-7000-8000-0000000000b3', 'Consultorio 1')
ON CONFLICT DO NOTHING;
INSERT INTO catalogos.coberturas (id, empresa_id, nombre, tipo) VALUES
    ('0192f000-0000-7000-8000-0000000000b5', '0192f000-0000-7000-8000-000000000001', 'Particular', 'PARTICULAR')
ON CONFLICT DO NOTHING;
INSERT INTO catalogos.profesionales (id, empresa_id, apellidos, nombres, matricula_provincial, usuario_id) VALUES
    ('0192f000-0000-7000-8000-0000000000b6', '0192f000-0000-7000-8000-000000000001', 'Local', 'Dra.', '12345',
     '0192f000-0000-7000-8000-0000000000a2')
ON CONFLICT DO NOTHING;
INSERT INTO catalogos.profesionales_especialidades (empresa_id, profesional_id, especialidad_id) VALUES
    ('0192f000-0000-7000-8000-000000000001', '0192f000-0000-7000-8000-0000000000b6', '0192f000-0000-7000-8000-0000000000b2')
ON CONFLICT DO NOTHING;
INSERT INTO turnos.agendas
    (id, empresa_id, profesional_id, servicio_id, consultorio_id, dia_semana, hora_desde, hora_hasta,
     duracion_minutos, vigente_desde, creado_por)
VALUES
    ('0192f000-0000-7000-8000-0000000000c1', '0192f000-0000-7000-8000-000000000001', '0192f000-0000-7000-8000-0000000000b6',
     '0192f000-0000-7000-8000-0000000000b1', '0192f000-0000-7000-8000-0000000000b4', 1, '09:00', '12:00', 20, '2026-01-01',
     '0192f000-0000-7000-8000-0000000000a3'),
    ('0192f000-0000-7000-8000-0000000000c2', '0192f000-0000-7000-8000-000000000001', '0192f000-0000-7000-8000-0000000000b6',
     '0192f000-0000-7000-8000-0000000000b1', '0192f000-0000-7000-8000-0000000000b4', 3, '09:00', '12:00', 20, '2026-01-01',
     '0192f000-0000-7000-8000-0000000000a3')
ON CONFLICT DO NOTHING;
