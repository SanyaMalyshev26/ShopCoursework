from __future__ import annotations

from datetime import date
from decimal import Decimal, ROUND_HALF_UP
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field, model_validator

MAX_PERIOD_DAYS = 366
CENT = Decimal("0.01")
TENTH = Decimal("0.1")


class DailySales(BaseModel):
    model_config = ConfigDict(extra="forbid")

    date: date
    orders: int = Field(ge=0)
    revenue: Decimal = Field(ge=0)
    unitsSold: int = Field(ge=0)


class SalesSummary(BaseModel):
    model_config = ConfigDict(extra="forbid")

    orders: int = Field(ge=0)
    revenue: Decimal = Field(ge=0)
    paidRevenue: Decimal = Field(ge=0)
    unitsSold: int = Field(ge=0)


class TopProduct(BaseModel):
    model_config = ConfigDict(extra="forbid")

    productId: int = Field(gt=0)
    productName: str = Field(min_length=1, max_length=120)
    unitsSold: int = Field(ge=0)
    revenue: Decimal = Field(ge=0)


class SalesReport(BaseModel):
    model_config = ConfigDict(extra="forbid")

    fromDate: date = Field(alias="from")
    toDate: date = Field(alias="to")
    currencyCode: str = Field(pattern=r"^[A-Z]{3}$")
    summary: SalesSummary
    daily: list[DailySales] = Field(max_length=MAX_PERIOD_DAYS)
    topProducts: list[TopProduct] = Field(max_length=10)

    @model_validator(mode="after")
    def validate_period_and_daily_dates(self) -> SalesReport:
        day_count = (self.toDate - self.fromDate).days + 1
        if day_count < 1 or day_count > MAX_PERIOD_DAYS:
            raise ValueError("Sales report period must be between 1 and 366 days")

        dates = [item.date for item in self.daily]
        if len(dates) != len(set(dates)):
            raise ValueError("Daily sales dates must be unique")
        if any(day < self.fromDate or day > self.toDate for day in dates):
            raise ValueError("Daily sales dates must be within the report period")
        return self


class BestSalesDay(BaseModel):
    date: date
    revenue: Decimal
    orders: int
    unitsSold: int


class SalesInsights(BaseModel):
    currencyCode: str
    averageOrderValue: Decimal | None
    averageDailyRevenue: Decimal
    bestSalesDay: BestSalesDay | None
    revenueChangePercent: Decimal | None
    revenueTrend: Literal["up", "down", "unchanged", "new_sales", "insufficient_data"]
    topProductsRevenueSharePercent: Decimal | None


def _rounded(value: Decimal, quantum: Decimal = CENT) -> Decimal:
    return value.quantize(quantum, rounding=ROUND_HALF_UP)


def analyze_sales(report: SalesReport) -> SalesInsights:
    day_count = (report.toDate - report.fromDate).days + 1
    average_order_value = (
        _rounded(report.summary.revenue / report.summary.orders)
        if report.summary.orders
        else None
    )
    average_daily_revenue = _rounded(report.summary.revenue / day_count)

    best_sales_day = max(
        report.daily,
        key=lambda item: (item.revenue, item.orders, -item.date.toordinal()),
        default=None,
    )
    best_day = (
        BestSalesDay(
            date=best_sales_day.date,
            revenue=best_sales_day.revenue,
            orders=best_sales_day.orders,
            unitsSold=best_sales_day.unitsSold,
        )
        if best_sales_day is not None and best_sales_day.revenue > 0
        else None
    )

    first_half_days = day_count // 2
    second_half_days = day_count - first_half_days
    if first_half_days == 0:
        revenue_change = None
        trend: Literal["up", "down", "unchanged", "new_sales", "insufficient_data"] = "insufficient_data"
    else:
        split_date = report.fromDate.fromordinal(report.fromDate.toordinal() + first_half_days)
        first_half_revenue = sum(
            (item.revenue for item in report.daily if item.date < split_date),
            Decimal("0"),
        )
        second_half_revenue = sum(
            (item.revenue for item in report.daily if item.date >= split_date),
            Decimal("0"),
        )
        first_half_average = first_half_revenue / first_half_days
        second_half_average = second_half_revenue / second_half_days
        if first_half_average == 0:
            revenue_change = None
            trend = "new_sales" if second_half_average > 0 else "unchanged"
        else:
            revenue_change = _rounded(
                ((second_half_average - first_half_average) / first_half_average) * 100,
                TENTH,
            )
            trend = (
                "up" if revenue_change > 0 else
                "down" if revenue_change < 0 else
                "unchanged"
            )

    top_products_revenue = sum(
        (product.revenue for product in report.topProducts),
        Decimal("0"),
    )
    top_products_share = (
        _rounded((top_products_revenue / report.summary.revenue) * 100, TENTH)
        if report.summary.revenue > 0
        else None
    )

    return SalesInsights(
        currencyCode=report.currencyCode,
        averageOrderValue=average_order_value,
        averageDailyRevenue=average_daily_revenue,
        bestSalesDay=best_day,
        revenueChangePercent=revenue_change,
        revenueTrend=trend,
        topProductsRevenueSharePercent=top_products_share,
    )
