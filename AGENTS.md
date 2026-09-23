# AGENTS.md — rag-api

## Tablero de tareas (Obsidian / NextCloud)

El vault de Obsidian **Desarrollo** está montado como directorio local:

```
~/obsidian-vault/          ← montaje rclone del vault (NextCloud → WebDAV)
```

- Servicio: `rclone-obsidian.service` (systemd de usuario, arranque automático).
- Los cambios de escritura se suben a NextCloud al instante: no hay copia ni sincronización pendiente.
- Si el directorio aparece vacío o "colgado", comprobar el servicio:
  `systemctl --user status rclone-obsidian.service`

### El tablero de este proyecto es un espejo generado

```
~/obsidian-vault/03_Projects/rag-api/TAREAS.md
```

⚠️ **No lo edites a mano: se sobrescribe.** Es un espejo de OpenSpec.

| Elemento | Ubicación |
|---|---|
| **Fuente de verdad** | `openspec/changes/*/tasks.md` del repo (una casilla por tarea) |
| **Generador** | `~/scripts/openspec-espejo.py` |
| **Destino** | `_obsidian/Desarrollo/03_Projects/rag-api/TAREAS.md` (vía WebDAV) |

**Flujo correcto para actualizar el estado:**

1. Marcar las casillas en `openspec/changes/<cambio>/tasks.md` (el trabajo real: `[x]` cuando esté verificado).
2. Regenerar el espejo:

```bash
python3 ~/scripts/openspec-espejo.py
```

Variables opcionales: `RAG_API_REPO` (por defecto `/opt/wf/rag-api`) y `OPENSPEC_CAMBIOS`
(lista separada por comas para publicar solo ciertos cambios).

Genera **una tarjeta por Unit**; el estado se deduce de las casillas:
todas hechas → `[x]`; alguna hecha → `[/]`; ninguna → `[ ]`.

### Otros tableros (editables a mano)

Los `TAREAS.md` de los demás proyectos del vault **sí** se editan directamente en el montaje
(`~/obsidian-vault/03_Projects/<proyecto>/TAREAS.md`).

Sintaxis kanban que entiende el Panel de Troya Hub:

| Marca | Estado |
|---|---|
| `- [ ]` | pendiente |
| `- [/]` | en curso |
| `- [x]` | hecho |
| `- [>]` | convertido |
| `- [~]` | archivado |

- `## Sección` agrupa las tareas siguientes; `#etiqueta` se extrae como tag (no usar `#` decorativos).
- Al reescribir un fichero completo se conserva la cabecera (`type: tareas`, `project: …`) y lo que ya no
  aplique **no se borra**: se mueve a una sección «Archivadas» con `- [~]` y el motivo.
