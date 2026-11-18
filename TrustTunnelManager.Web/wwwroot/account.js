window.trustTunnelManager = {
    logout: async () => {
        const tokenResponse = await fetch("/account/antiforgery-token", {
            method: "GET",
            credentials: "same-origin",
            cache: "no-store"
        });

        if (!tokenResponse.ok) {
            throw new Error("Не удалось получить токен безопасности.");
        }

        const { requestToken } = await tokenResponse.json();
        const response = await fetch("/account/logout", {
            method: "POST",
            credentials: "same-origin",
            headers: {
                "RequestVerificationToken": requestToken
            }
        });

        if (!response.ok && response.redirected !== true) {
            throw new Error("Не удалось завершить сеанс.");
        }

        window.location.assign("/login");
    }
};
