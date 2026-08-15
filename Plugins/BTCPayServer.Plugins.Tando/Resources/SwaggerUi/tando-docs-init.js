window.addEventListener("DOMContentLoaded", () => {
    var el = document.getElementById("swagger-ui");
    SwaggerUIBundle({
        url: el.getAttribute("data-spec-url"),
        dom_id: "#swagger-ui",
        presets: [SwaggerUIBundle.presets.apis]
    });
});