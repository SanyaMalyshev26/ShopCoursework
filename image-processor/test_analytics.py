import unittest
from decimal import Decimal

from pydantic import ValidationError

from analytics import SalesReport, analyze_sales


class SalesAnalyticsTests(unittest.TestCase):
    def test_analyze_sales_returns_average_best_day_and_half_period_trend(self):
        report = SalesReport.model_validate(
            {
                "from": "2026-10-01",
                "to": "2026-10-04",
                "currencyCode": "RUB",
                "summary": {
                    "orders": 3,
                    "revenue": "600.00",
                    "paidRevenue": "200.00",
                    "unitsSold": 5,
                },
                "daily": [
                    {"date": "2026-10-01", "orders": 1, "revenue": "100.00", "unitsSold": 1},
                    {"date": "2026-10-02", "orders": 1, "revenue": "100.00", "unitsSold": 1},
                    {"date": "2026-10-03", "orders": 0, "revenue": "0", "unitsSold": 0},
                    {"date": "2026-10-04", "orders": 1, "revenue": "400.00", "unitsSold": 3},
                ],
                "topProducts": [
                    {
                        "productId": 1,
                        "productName": "Jeans",
                        "unitsSold": 4,
                        "revenue": "500.00",
                    }
                ],
            }
        )

        insights = analyze_sales(report)

        self.assertEqual(Decimal("200.00"), insights.averageOrderValue)
        self.assertEqual(Decimal("150.00"), insights.averageDailyRevenue)
        self.assertEqual("2026-10-04", insights.bestSalesDay.date.isoformat())
        self.assertEqual(Decimal("100.0"), insights.revenueChangePercent)
        self.assertEqual("up", insights.revenueTrend)
        self.assertEqual(Decimal("83.3"), insights.topProductsRevenueSharePercent)

    def test_empty_sales_report_has_no_division_errors_or_personal_fields(self):
        report = SalesReport.model_validate(
            {
                "from": "2026-10-01",
                "to": "2026-10-01",
                "currencyCode": "RUB",
                "summary": {
                    "orders": 0,
                    "revenue": "0",
                    "paidRevenue": "0",
                    "unitsSold": 0,
                },
                "daily": [],
                "topProducts": [],
            }
        )

        insights = analyze_sales(report)

        self.assertIsNone(insights.averageOrderValue)
        self.assertEqual(Decimal("0.00"), insights.averageDailyRevenue)
        self.assertIsNone(insights.bestSalesDay)
        self.assertIsNone(insights.revenueChangePercent)
        self.assertEqual("insufficient_data", insights.revenueTrend)
        self.assertIsNone(insights.topProductsRevenueSharePercent)
        self.assertNotIn("userId", insights.model_dump_json())
        self.assertIn('"averageDailyRevenue":"0.00"', insights.model_dump_json())

    def test_rejects_report_with_personal_fields_or_daily_date_outside_period(self):
        payload = {
            "from": "2026-10-01",
            "to": "2026-10-01",
            "currencyCode": "RUB",
            "summary": {
                "orders": 0,
                "revenue": "0",
                "paidRevenue": "0",
                "unitsSold": 0,
            },
            "daily": [],
            "topProducts": [],
        }
        with self.assertRaises(ValidationError):
            SalesReport.model_validate({**payload, "email": "buyer@example.invalid"})

        with self.assertRaises(ValidationError):
            SalesReport.model_validate(
                {
                    **payload,
                    "daily": [
                        {
                            "date": "2026-10-02",
                            "orders": 1,
                            "revenue": "10",
                            "unitsSold": 1,
                        }
                    ],
                }
            )


if __name__ == "__main__":
    unittest.main()
