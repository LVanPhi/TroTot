"""PostgreSQL connection pool.

DATABASE_URL must use the app_worker role (least privilege, docs/07-security.md).
"""

from psycopg.rows import dict_row
from psycopg_pool import ConnectionPool


def make_pool(database_url: str, max_size: int = 4) -> ConnectionPool:
    pool = ConnectionPool(
        database_url,
        min_size=1,
        max_size=max_size,
        kwargs={"row_factory": dict_row, "options": "-c search_path=app"},
        open=False,
    )
    pool.open(wait=True, timeout=30)
    return pool
