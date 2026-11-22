"""Read-only S3 access to Garage with the troviec-worker key (scan-images, listing-images)."""

from collections.abc import Callable

import boto3
from botocore.config import Config

from troviec_worker.config import Settings

ImageLoader = Callable[[str, str], bytes]


def make_image_loader(settings: Settings) -> ImageLoader:
    client = boto3.client(
        "s3",
        endpoint_url=settings.s3_endpoint,
        region_name=settings.s3_region,
        aws_access_key_id=settings.s3_access_key,
        aws_secret_access_key=settings.s3_secret_key,
        config=Config(
            s3={"addressing_style": "path"},
            # Garage rejects the CRC checksums newer SDKs add by default (same fix as the API).
            request_checksum_calculation="when_required",
            response_checksum_validation="when_required",
            connect_timeout=3,
            read_timeout=5,
            retries={"max_attempts": 2},
        ),
    )

    def load(bucket: str, key: str) -> bytes:
        return client.get_object(Bucket=bucket, Key=key)["Body"].read()

    return load
