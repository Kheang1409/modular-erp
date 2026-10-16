# Third-party notices

The application includes Bootstrap 5.3.8 by the Bootstrap Authors, licensed under the MIT License.

Bootstrap's CSS and license are restored from the pinned package in
`src/Platform/Erp.Web/libman.json` by Microsoft Library Manager during the .NET build.
They are packaged and served from the application itself; users do not need access
to a public CDN to use the interface.

The unmodified Bootstrap license is included at
`src/Platform/Erp.Web/wwwroot/vendor/bootstrap/LICENSE` after a build.

Bootstrap: <https://getbootstrap.com/>

License source: <https://github.com/twbs/bootstrap/blob/v5.3.8/LICENSE>

## Integration-test storage fixture

The integration suite builds and runs MinIO from upstream revision
`7aac2a2c5b7c882e68c1ce017d8256be2feea27f` in a disposable container.
MinIO is licensed under AGPL-3.0; it is not bundled in the application image.
Open ERP's Apache-2.0 license does not replace third-party licenses.

Source and license:
<https://github.com/minio/minio/tree/7aac2a2c5b7c882e68c1ce017d8256be2feea27f>
and <https://github.com/minio/minio/blob/7aac2a2c5b7c882e68c1ce017d8256be2feea27f/LICENSE>.
