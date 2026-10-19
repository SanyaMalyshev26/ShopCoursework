from __future__ import annotations

import io
import os
import tempfile
import uuid
import warnings
from pathlib import Path

from fastapi import FastAPI, File, HTTPException, UploadFile
from PIL import Image, ImageOps, UnidentifiedImageError
from pydantic import BaseModel

from analytics import SalesInsights, SalesReport, analyze_sales

MAX_UPLOAD_BYTES = 10 * 1024 * 1024
MAX_IMAGE_PIXELS = 25_000_000
MAX_SOURCE_SIDE = 16_000
UPLOAD_DIRECTORY = Path(os.environ.get("UPLOAD_DIRECTORY", "/uploads"))
SUPPORTED_FORMATS = {"JPEG", "PNG", "WEBP"}

Image.MAX_IMAGE_PIXELS = MAX_IMAGE_PIXELS

app = FastAPI(title="ShopCoursework image processor", docs_url=None, redoc_url=None)


class ProcessedImage(BaseModel):
    imageUrl: str
    thumbnailUrl: str
    width: int
    height: int


def _to_webp(image: Image.Image, size: tuple[int, int]) -> bytes:
    image = image.copy()
    image.thumbnail(size, Image.Resampling.LANCZOS)
    output = io.BytesIO()
    image.save(output, format="WEBP", quality=82, method=6)
    return output.getvalue()


def _write_file(name: str, content: bytes) -> None:
    temporary_path: str | None = None
    try:
        with tempfile.NamedTemporaryFile(dir=UPLOAD_DIRECTORY, delete=False) as temporary_file:
            temporary_path = temporary_file.name
            temporary_file.write(content)
        os.replace(temporary_path, UPLOAD_DIRECTORY / name)
    except OSError:
        if temporary_path is not None:
            Path(temporary_path).unlink(missing_ok=True)
        raise


@app.get("/health")
def health() -> dict[str, str]:
    if not UPLOAD_DIRECTORY.is_dir() or not os.access(UPLOAD_DIRECTORY, os.W_OK):
        raise HTTPException(status_code=503, detail="Upload storage is unavailable")
    return {"status": "ok"}


@app.post("/images", response_model=ProcessedImage)
async def process_image(image: UploadFile = File(...)) -> ProcessedImage:
    try:
        if image.content_type not in {"image/jpeg", "image/png", "image/webp"}:
            raise HTTPException(status_code=415, detail="Only JPEG, PNG, and WebP images are supported")

        content = await image.read(MAX_UPLOAD_BYTES + 1)
        if not content:
            raise HTTPException(status_code=400, detail="The uploaded image is empty")
        if len(content) > MAX_UPLOAD_BYTES:
            raise HTTPException(status_code=413, detail="The uploaded image exceeds 10 MB")

        try:
            with warnings.catch_warnings():
                warnings.simplefilter("error", Image.DecompressionBombWarning)
                with Image.open(io.BytesIO(content)) as source:
                    if source.format not in SUPPORTED_FORMATS:
                        raise HTTPException(status_code=415, detail="Unsupported image format")
                    if source.width > MAX_SOURCE_SIDE or source.height > MAX_SOURCE_SIDE:
                        raise HTTPException(status_code=422, detail="Image dimensions are too large")
                    source.load()
                    oriented = ImageOps.exif_transpose(source)
                    converted = oriented.convert("RGBA" if "A" in oriented.getbands() else "RGB")
                    width, height = converted.size
                    main_content = _to_webp(converted, (1600, 1600))
                    thumbnail_content = _to_webp(converted, (400, 400))
                    scale = min(1, 1600 / max(width, height))
                    output_width = round(width * scale)
                    output_height = round(height * scale)
        except HTTPException:
            raise
        except (
            Image.DecompressionBombError,
            Image.DecompressionBombWarning,
            UnidentifiedImageError,
            OSError,
            ValueError,
        ) as exception:
            raise HTTPException(
                status_code=422,
                detail="The uploaded file is not a valid supported image",
            ) from exception

        identifier = uuid.uuid4().hex
        main_name = f"{identifier}.webp"
        thumbnail_name = f"{identifier}_thumb.webp"
        try:
            _write_file(main_name, main_content)
            _write_file(thumbnail_name, thumbnail_content)
        except OSError as exception:
            (UPLOAD_DIRECTORY / main_name).unlink(missing_ok=True)
            (UPLOAD_DIRECTORY / thumbnail_name).unlink(missing_ok=True)
            raise HTTPException(status_code=503, detail="Upload storage is unavailable") from exception

        return ProcessedImage(
            imageUrl=f"/uploads/{main_name}",
            thumbnailUrl=f"/uploads/{thumbnail_name}",
            width=output_width,
            height=output_height,
        )
    finally:
        await image.close()


@app.post("/analytics/sales-insights")
def sales_insights(report: SalesReport) -> SalesInsights:
    return analyze_sales(report)
