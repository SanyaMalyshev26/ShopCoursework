document.querySelectorAll('.size-recommendation-form').forEach((form) => {
    form.addEventListener('submit', async (event) => {
        event.preventDefault();

        const result = form.querySelector('.size-recommendation-result');
        const measurements = {};
        form.querySelectorAll('[data-measure]').forEach((input) => {
            if (input.value !== '') {
                measurements[input.dataset.measure] = Number(input.value);
            }
        });

        if (Object.keys(measurements).length === 0) {
            result.textContent = 'Укажите хотя бы одну мерку.';
            return;
        }

        result.textContent = 'Подбираем размер…';
        try {
            const response = await fetch(form.dataset.url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(measurements)
            });
            const data = await response.json();
            if (!response.ok) {
                throw new Error(data.message || 'Не удалось подобрать размер.');
            }

            if (!data.recommendedSize) {
                result.textContent = data.message;
                return;
            }

            const variantSelect = form.closest('.product-card')?.querySelector('.variant-select');
            if (variantSelect) {
                const matchingVariant = Array.from(variantSelect.options).find((option) =>
                    option.dataset.size?.toUpperCase() === data.recommendedSize.toUpperCase() &&
                    !option.disabled
                );
                if (matchingVariant) {
                    variantSelect.value = matchingVariant.value;
                    variantSelect.dispatchEvent(new Event('change', { bubbles: true }));
                }
            }

            const options = data.matchingSizes
                .map((item) => `${item.size} (${item.availableStock} шт.)`)
                .join(', ');
            result.textContent = `Рекомендуем ${data.recommendedSize}. В наличии: ${data.availableStock}. Подходящие размеры: ${options}. Выбран доступный вариант; при необходимости измените цвет. ${data.message}`;
        } catch (error) {
            result.textContent = error.message || 'Сервис подбора временно недоступен.';
        }
    });
});
